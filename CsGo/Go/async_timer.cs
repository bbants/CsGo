﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Threading;
using System.Runtime.InteropServices;
using System.Diagnostics;

namespace Go
{
    public class system_tick
    {
        [DllImport("kernel32.dll")]
        private static extern bool QueryPerformanceCounter(out long lpPerformanceCount);
        [DllImport("kernel32.dll")]
        private static extern bool QueryPerformanceFrequency(out long frequency);

        private static system_tick _pcCycle = new system_tick();
#if DEBUG
        private static volatile bool _checkStepDebugSign = false;
#endif

        private double _sCycle;
        private double _msCycle;
        private double _usCycle;

        private system_tick()
        {
            long freq = 0;
            if (!QueryPerformanceFrequency(out freq))
            {
                _sCycle = 0;
                _msCycle = 0;
                _usCycle = 0;
                return;
            }
            _sCycle = 1.0 / (double)freq;
            _msCycle = 1000.0 / (double)freq;
            _usCycle = 1000000.0 / (double)freq;
#if DEBUG
            Thread checkStepDebug = new Thread(delegate ()
            {
                long checkTick = get_tick_ms();
                while (true)
                {
                    Thread.Sleep(80);
                    long oldTick = checkTick;
                    checkTick = get_tick_ms();
                    _checkStepDebugSign = (checkTick - oldTick) > 100;
                }
            });
            checkStepDebug.Priority = ThreadPriority.Highest;
            checkStepDebug.IsBackground = true;
            checkStepDebug.Name = "单步调试检测";
            checkStepDebug.Start();
#endif
        }

        public static long get_tick_us()
        {
            long quadPart = 0;
            QueryPerformanceCounter(out quadPart);
            return (long)((double)quadPart * _pcCycle._usCycle);
        }

        public static long get_tick_ms()
        {
            long quadPart = 0;
            QueryPerformanceCounter(out quadPart);
            return (long)((double)quadPart * _pcCycle._msCycle);
        }

        public static int get_tick_s()
        {
            long quadPart = 0;
            QueryPerformanceCounter(out quadPart);
            return (int)((double)quadPart * _pcCycle._sCycle);
        }

#if DEBUG
        public static bool check_step_debugging()
        {
            return _checkStepDebugSign;
        }
#endif
    }

    public class async_timer
    {
        struct steady_timer_handle
        {
            public long absus;
            public long period;
            public MapNode<long, async_timer> node;
        }

        internal class steady_timer
        {
            struct waitable_event_handle
            {
                public int id;
                public steady_timer steadyTimer;

                public waitable_event_handle(int i, steady_timer h)
                {
                    id = i;
                    steadyTimer = h;
                }
            }

            class waitable_timer
            {
                [DllImport("kernel32.dll")]
                private static extern int CreateWaitableTimer(int lpTimerAttributes, int bManualReset, int lpTimerName);
                [DllImport("kernel32.dll")]
                private static extern int SetWaitableTimer(int hTimer, ref long pDueTime, int lPeriod, int pfnCompletionRoutine, int lpArgToCompletionRoutine, int fResume);
                [DllImport("kernel32.dll")]
                private static extern int CancelWaitableTimer(int hTimer);
                [DllImport("kernel32.dll")]
                private static extern int CloseHandle(int hObject);
                [DllImport("kernel32.dll")]
                private static extern int WaitForSingleObject(int hHandle, int dwMilliseconds);
                [DllImport("NtDll.dll")]
                private static extern int NtQueryTimerResolution(out uint MaximumTime, out uint MinimumTime, out uint CurrentTime);
                [DllImport("NtDll.dll")]
                private static extern int NtSetTimerResolution(uint DesiredTime, uint SetResolution, out uint ActualTime);

                static public readonly waitable_timer timer = new waitable_timer();

                bool _exited;
                int _timerHandle;
                long _expireTime;
                Thread _timerThread;
                work_engine _workEngine;
                work_strand _workStrand;
                Map<long, waitable_event_handle> _eventsQueue;

                waitable_timer()
                {
                    _exited = false;
                    _expireTime = long.MaxValue;
                    _eventsQueue = new Map<long, waitable_event_handle>(true);
                    _timerHandle = CreateWaitableTimer(0, 0, 0);
                    _workEngine = new work_engine();
                    _workStrand = new work_strand(_workEngine);
                    _timerThread = new Thread(timerThread);
                    _timerThread.Priority = ThreadPriority.Highest;
                    _timerThread.IsBackground = true;
                    _timerThread.Name = "定时器调度";
                    _workEngine.run(1, ThreadPriority.Highest, true);
                    _timerThread.Start();
                    uint MaximumTime = 0, MinimumTime = 0, CurrentTime = 0, ActualTime = 0;
                    if (0 == NtQueryTimerResolution(out MaximumTime, out MinimumTime, out CurrentTime))
                    {
                        NtSetTimerResolution(MinimumTime, 1, out ActualTime);
                    }
                }

                ~waitable_timer()
                {
                    /*_workStrand.post(delegate ()
                    {
                        _exited = true;
                        long sleepTime = 0;
                        SetWaitableTimer(_timerHandle, ref sleepTime, 0, 0, 0, 0);
                    });
                    _timerThread.Join();
                    _workEngine.stop();*/
                    CloseHandle(_timerHandle);
                }

                public void appendEvent(long absus, waitable_event_handle eventHandle)
                {
                    _workStrand.post(delegate ()
                    {
                        eventHandle.steadyTimer._waitableNode = _eventsQueue.Insert(absus, eventHandle);
                        if (absus < _expireTime)
                        {
                            _expireTime = absus;
                            long sleepTime = -(absus - system_tick.get_tick_us()) * 10;
                            sleepTime = sleepTime < 0 ? sleepTime : 0;
                            SetWaitableTimer(_timerHandle, ref sleepTime, 0, 0, 0, 0);
                        }
                    });
                }

                public void removeEvent(steady_timer steadyTime)
                {
                    _workStrand.post(delegate ()
                    {
                        if (null != steadyTime._waitableNode)
                        {
                            long lastAbsus = steadyTime._waitableNode.Key;
                            _eventsQueue.Remove(steadyTime._waitableNode);
                            steadyTime._waitableNode = null;
                            if (0 == _eventsQueue.Count)
                            {
                                _expireTime = long.MaxValue;
                                CancelWaitableTimer(_timerHandle);
                            }
                            else if (lastAbsus == _expireTime)
                            {
                                _expireTime = _eventsQueue.First.Key;
                                long sleepTime = -(_expireTime - system_tick.get_tick_us()) * 10;
                                sleepTime = sleepTime < 0 ? sleepTime : 0;
                                SetWaitableTimer(_timerHandle, ref sleepTime, 0, 0, 0, 0);
                            }
                        }
                    });
                }

                public void updateEvent(long absus, waitable_event_handle eventHandle)
                {
                    _workStrand.post(delegate ()
                    {
                        if (null != eventHandle.steadyTimer._waitableNode)
                        {
                            _eventsQueue.Insert(_eventsQueue.ReNewNode(eventHandle.steadyTimer._waitableNode, absus, eventHandle));
                        }
                        else
                        {
                            eventHandle.steadyTimer._waitableNode = _eventsQueue.Insert(absus, eventHandle);
                        }
                        long newAbsus = _eventsQueue.First.Key;
                        if (newAbsus < _expireTime)
                        {
                            _expireTime = newAbsus;
                            long sleepTime = -(newAbsus - system_tick.get_tick_us()) * 10;
                            sleepTime = sleepTime < 0 ? sleepTime : 0;
                            SetWaitableTimer(_timerHandle, ref sleepTime, 0, 0, 0, 0);
                        }
                    });
                }

                private void timerThread()
                {
                    while (0 == WaitForSingleObject(_timerHandle, -1) && !_exited)
                    {
                        _workStrand.post(timerComplete);
                    }
                }

                private void timerComplete()
                {
                    _expireTime = long.MaxValue;
                    while (0 != _eventsQueue.Count)
                    {
                        MapNode<long, waitable_event_handle> first = _eventsQueue.First;
                        long absus = first.Key;
                        long ct = system_tick.get_tick_us();
                        if (absus > ct)
                        {
                            _expireTime = absus;
                            long sleepTime = -(absus - ct) * 10;
                            SetWaitableTimer(_timerHandle, ref sleepTime, 0, 0, 0, 0);
                            break;
                        }
                        first.Value.steadyTimer._waitableNode = null;
                        first.Value.steadyTimer.timer_handler(first.Value.id);
                        _eventsQueue.Remove(first);
                    }
                }
            }

            bool _looping;
            int _timerCount;
            long _expireTime;
            shared_strand _strand;
            MapNode<long, waitable_event_handle> _waitableNode;
            Map<long, async_timer> _timerQueue;

            public steady_timer(shared_strand strand)
            {
                _timerCount = 0;
                _looping = false;
                _expireTime = long.MaxValue;
                _strand = strand;
                _timerQueue = new Map<long, async_timer>(true);
            }

            public void timeout(async_timer asyncTimer)
            {
                long absus = asyncTimer._timerHandle.absus;
                asyncTimer._timerHandle.node = _timerQueue.Insert(absus, asyncTimer);
                if (!_looping)
                {
                    _looping = true;
                    _expireTime = absus;
                    timer_loop(absus);
                }
                else if (absus < _expireTime)
                {
                    _expireTime = absus;
                    timer_reloop(absus);
                }
            }

            public void cancel(async_timer asyncTimer)
            {
                if (null != asyncTimer._timerHandle.node)
                {
                    _timerQueue.Remove(asyncTimer._timerHandle.node);
                    asyncTimer._timerHandle.node = null;
                    if (0 == _timerQueue.Count)
                    {
                        _timerCount++;
                        _expireTime = 0;
                        _looping = false;
                        waitable_timer.timer.removeEvent(this);
                    }
                    else if (asyncTimer._timerHandle.absus == _expireTime)
                    {
                        _expireTime = _timerQueue.First.Key;
                        timer_reloop(_expireTime);
                    }
                }
            }

            public void re_timeout(async_timer asyncTimer)
            {
                long absus = asyncTimer._timerHandle.absus;
                if (null != asyncTimer._timerHandle.node)
                {
                    _timerQueue.Insert(_timerQueue.ReNewNode(asyncTimer._timerHandle.node, absus, asyncTimer));
                }
                else
                {
                    asyncTimer._timerHandle.node = _timerQueue.Insert(absus, asyncTimer);
                }
                long newAbsus = _timerQueue.First.Key;
                if (!_looping)
                {
                    _looping = true;
                    _expireTime = newAbsus;
                    timer_loop(newAbsus);
                }
                else if (newAbsus < _expireTime)
                {
                    _expireTime = newAbsus;
                    timer_reloop(newAbsus);
                }
            }

            public void timer_handler(int id)
            {
                if (id != _timerCount)
                {
                    return;
                }
                _strand.post(delegate ()
                {
                    if (id == _timerCount)
                    {
                        _expireTime = long.MinValue;
                        while (0 != _timerQueue.Count)
                        {
                            MapNode<long, async_timer> first = _timerQueue.First;
                            if (first.Key > system_tick.get_tick_us())
                            {
                                _expireTime = first.Key;
                                timer_loop(_expireTime);
                                return;
                            }
                            else
                            {
                                first.Value._timerHandle.node = null;
                                first.Value.timer_handler();
                                _timerQueue.Remove(first);
                            }
                        }
                        _looping = false;
                    }
                });
            }

            void timer_loop(long absus)
            {
                waitable_timer.timer.appendEvent(absus, new waitable_event_handle(++_timerCount, this));
            }

            void timer_reloop(long absus)
            {
                waitable_timer.timer.updateEvent(absus, new waitable_event_handle(++_timerCount, this));
            }
        }

        shared_strand _strand;
        functional.func _handler;
        steady_timer_handle _timerHandle;
        int _timerCount;
        long _beginTick;
        bool _isInterval;
        bool _onTopCall;

        public async_timer(shared_strand strand)
        {
            _strand = strand;
            _timerCount = 0;
            _beginTick = 0;
            _isInterval = false;
            _onTopCall = false;
        }

        public shared_strand self_strand()
        {
            return _strand;
        }

        private void timer_handler()
        {
            _onTopCall = true;
            if (_isInterval)
            {
                int lastTc = _timerCount;
                _handler();
                if (lastTc == _timerCount)
                {
                    begin_timer(_timerHandle.absus += _timerHandle.period, _timerHandle.period);
                }
            }
            else
            {
                functional.func handler = _handler;
                _handler = null;
                _strand.release_work();
                handler();
            }
            _onTopCall = false;
        }

        private void begin_timer(long absus, long period)
        {
            _timerCount++;
            _timerHandle.absus = absus;
            _timerHandle.period = period;
            _strand._timer.timeout(this);
        }

        private void re_begin_timer(long absus, long period)
        {
            _timerCount++;
            _timerHandle.absus = absus;
            _timerHandle.period = period;
            _strand._timer.re_timeout(this);
        }

        public void timeout_us(long us, functional.func handler)
        {
#if DEBUG
            Trace.Assert(_strand.running_in_this_thread() && null == _handler && null != handler);
#endif
            _isInterval = false;
            _handler = handler;
            _strand.hold_work();
            _beginTick = system_tick.get_tick_us();
            begin_timer(_beginTick + us, us);
        }

        public void deadline_us(long us, functional.func handler)
        {
#if DEBUG
            Trace.Assert(_strand.running_in_this_thread() && null == _handler && null != handler);
#endif
            _isInterval = false;
            _handler = handler;
            _strand.hold_work();
            _beginTick = system_tick.get_tick_us();
            begin_timer(us, us - _beginTick);
        }

        public void timeout(int ms, functional.func handler)
        {
            timeout_us((long)ms * 1000, handler);
        }

        public void deadline(long ms, functional.func handler)
        {
            deadline_us(ms * 1000, handler);
        }

        public void interval(int ms, functional.func handler, bool immed = false)
        {
            interval_us((long)ms * 1000, handler, immed);
        }

        public void interval2(int ms1, int ms2, functional.func handler, bool immed = false)
        {
            interval2_us((long)ms1 * 1000, (long)ms2 * 1000, handler, immed);
        }

        public void interval_us(long us, functional.func handler, bool immed = false)
        {
            interval2_us(us, us, handler, immed);
        }

        public void interval2_us(long us1, long us2, functional.func handler, bool immed = false)
        {
#if DEBUG
            Trace.Assert(_strand.running_in_this_thread() && null == _handler && null != handler);
#endif
            _isInterval = true;
            _handler = handler;
            _strand.hold_work();
            _beginTick = system_tick.get_tick_us();
            begin_timer(_beginTick + us1, us2);
            if (immed)
            {
                handler();
            }
        }

        public bool restart(int ms = -1)
        {
            return restart_us(0 > ms ? -1 : (long)ms * 1000);
        }

        public bool restart_us(long us = -1)
        {
#if DEBUG
            Trace.Assert(_strand.running_in_this_thread());
#endif
            if (null != _handler)
            {
                _beginTick = system_tick.get_tick_us();
                if (0 > us)
                {
                    re_begin_timer(_beginTick + _timerHandle.period, _timerHandle.period);
                }
                else
                {
                    re_begin_timer(_beginTick + us, us);
                }
                return true;
            }
            return false;
        }

        public bool advance()
        {
#if DEBUG
            Trace.Assert(_strand.running_in_this_thread());
#endif
            if (null != _handler)
            {
                if (!_isInterval)
                {
                    functional.func handler = _handler;
                    cancel();
                    handler();
                    return true;
                }
                else if (!_onTopCall)
                {
                    _handler();
                    return true;
                }
            }
            return false;
        }

        public long cancel()
        {
#if DEBUG
            Trace.Assert(_strand.running_in_this_thread());
#endif
            if (null != _handler)
            {
                _timerCount++;
                _strand._timer.cancel(this);
                long lastBegin = _beginTick;
                _beginTick = 0;
                _handler = null;
                _strand.release_work();
                return lastBegin;
            }
            return 0;
        }
    }
}
