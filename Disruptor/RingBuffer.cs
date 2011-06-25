/**
 * Ring based store of reusable entries containing the data representing an {@link IEntry} being exchanged between producers and consumers.
 *
 * @param <T> IEntry implementation storing the data for sharing during exchange or parallel coordination of an event.
 */

using System;
using System.Threading;

namespace Disruptor
{
    public interface IRingBuffer
    {
        int Capacity { get; }
        long Cursor { get; }
    }

    public interface IRingBuffer<T> : IRingBuffer where T : IEntry
    {
        IConsumerBarrier<T> CreateConsumerBarrier(params IConsumer[] consumersToTrack);
        IProducerBarrier<T> CreateProducerBarrier(params IConsumer[] consumersToTrack);
        IForceFillProducerBarrier<T> CreateForceFillProducerBarrier(params IConsumer[] consumersToTrack);
        T GetEntry(long sequence);
    }

    public class RingBuffer<T> : IRingBuffer<T> where T : IEntry
    {
        public const long InitialCursorValue = -1L;

        public long p1, p2, p3, p4, p5, p6, p7; // cache line padding

        private long _cursor = InitialCursorValue;

        public long p8, p9, p10, p11, p12, p13, p14; // cache line padding
		
		public long Cursor
		{
			get { return Thread.VolatileRead(ref _cursor); }
			private set { Thread.VolatileWrite(ref _cursor, value); }
		}

        private readonly T[] _entries;
        private readonly int _ringModMask;

        private readonly IEntryFactory<T> _entryFactory;
        private readonly IClaimStrategy _claimStrategy;
        private readonly IWaitStrategy<T> _waitStrategy;

        public RingBuffer(IEntryFactory<T> entryFactory, 
                        int size,
                          IClaimStrategy claimStrategyOption,
                          IWaitStrategy<T> waitStrategyOption)
        {
            int sizeAsPowerOfTwo = Util.CeilingNextPowerOfTwo(size);
            _ringModMask = sizeAsPowerOfTwo - 1;
            _entries = new T[sizeAsPowerOfTwo];

            _entryFactory = entryFactory;
            _claimStrategy = claimStrategyOption;
            _waitStrategy = waitStrategyOption;

            Fill();
        }

        public RingBuffer(IEntryFactory<T> entryFactory, int size)
            : this(entryFactory, size, new SingleThreadedStrategy(), new YieldingStrategy<T>())
        {
        }

        public IConsumerBarrier<T> CreateConsumerBarrier(params IConsumer[] consumersToTrack)
        {
            return new ConsumerTrackingConsumerBarrier<T>(this, consumersToTrack);
        }

        public IProducerBarrier<T> CreateProducerBarrier(params IConsumer[] consumersToTrack)
        {
            return new ConsumerTrackingProducerBarrier(this, consumersToTrack);
        }

        public IForceFillProducerBarrier<T> CreateForceFillProducerBarrier(params IConsumer[] consumersToTrack)
        {
            return new ForceFillConsumerTrackingProducerBarrier(this, consumersToTrack);
        }
		
		public int Capacity { get { return _entries.Length; } }

        public T GetEntry(long sequence)
        {
             unchecked
            {
              return  _entries[(int) sequence & _ringModMask];
            }
            
        }

        private void Fill()
        {
            for (int i = 0; i < _entries.Length; i++)
            {
                _entries[i] = _entryFactory.Create();
            }
        }

        private class ConsumerTrackingConsumerBarrier<T> : IConsumerBarrier<T> where T : IEntry
        {
            public long p1, p2, p3, p4, p5, p6, p7; // cache line padding
            private readonly IConsumer[] _consumers;
            private volatile bool _alerted;
            public long p8, p9, p10, p11, p12, p13, p14; // cache line padding

            private readonly RingBuffer<T> _ringBuffer;

            public ConsumerTrackingConsumerBarrier(RingBuffer<T> ringBuffer, IConsumer[] consumers)
            {
                _ringBuffer = ringBuffer;
                this._consumers = consumers;
            }
            
            public T GetEntry(long sequence)
            {
                unchecked
                {
                    return _ringBuffer._entries[(int) sequence & _ringBuffer._ringModMask];
                }
            }


            public long WaitFor(long sequence)
            {
                return _ringBuffer._waitStrategy.WaitFor(_consumers, _ringBuffer, this, sequence);
            }


            public long WaitFor(long sequence, long timeout)
            {
                return _ringBuffer._waitStrategy.WaitFor(_consumers, _ringBuffer, this, sequence, timeout);
            }


            public long GetCursor()
            {
                return _ringBuffer.Cursor;
            }


            public bool IsAlerted()
            {
                return _alerted;
            }


            public void Alert()
            {
                _alerted = true;
                _ringBuffer._waitStrategy.SignalAll();
            }


            public void ClearAlert()
            {
                _alerted = false;
            }
        }

        private class ConsumerTrackingProducerBarrier : IProducerBarrier<T>
        {
            private readonly RingBuffer<T> _ringBuffer;
            private readonly IConsumer[] _consumers;

            public ConsumerTrackingProducerBarrier(RingBuffer<T> ringBuffer, IConsumer[] consumers)
            {
                if (0 == consumers.Length)
                {
                    throw new ArgumentOutOfRangeException(
                        "There must be at least one IConsumer to track for preventing ring wrap");
                }

                _ringBuffer = ringBuffer;
                this._consumers = consumers;
            }


            //    @SuppressWarnings("unchecked")
            public T NextEntry()
            {
                long sequence = _ringBuffer._claimStrategy.GetAndIncrement();
                EnsureConsumersAreInRange(sequence);

                T entry = _ringBuffer._entries[(int) sequence & _ringBuffer._ringModMask];
                entry.Sequence = sequence;

                return entry;
            }


            public void Commit(T entry)
            {
                long sequence = entry.Sequence;
                _ringBuffer._claimStrategy.WaitForCursor(sequence - 1L, _ringBuffer);
                _ringBuffer.Cursor = sequence;
                _ringBuffer._waitStrategy.SignalAll();
            }


            public long GetCursor()
            {
                return _ringBuffer.Cursor;
            }

            private void EnsureConsumersAreInRange(long sequence)
            {
                while ((sequence - Util.GetMinimumSequence(_consumers)) >= _ringBuffer._entries.Length)
                {
                    Thread.Yield();
                }
            }
        }

        private class ForceFillConsumerTrackingProducerBarrier : IForceFillProducerBarrier<T>
        {
            private readonly IConsumer[] _consumers;
            private readonly RingBuffer<T> _ringBuffer;

            public ForceFillConsumerTrackingProducerBarrier(RingBuffer<T> ringBuffer, IConsumer[] consumers)
            {
                if (0 == consumers.Length)
                {
                    throw new ArgumentOutOfRangeException(
                        "There must be at least one IConsumer to track for preventing ring wrap");
                }

                _ringBuffer = ringBuffer;
                _consumers = consumers;
            }


            //    @SuppressWarnings("unchecked")
            public T ClaimEntry(long sequence)
            {
                EnsureConsumersAreInRange(sequence);

                unchecked
                {
                    T entry = _ringBuffer._entries[(int) sequence & _ringBuffer._ringModMask];
                    entry.Sequence = sequence;

                    return entry;
                }
            }


            public void Commit(T entry)
            {
                long sequence = entry.Sequence;
                _ringBuffer._claimStrategy.SetSequence(sequence + 1L);
                _ringBuffer.Cursor = sequence;
                _ringBuffer._waitStrategy.SignalAll();
            }


            public long GetCursor()
            {
                return _ringBuffer.Cursor;
            }

            private void EnsureConsumersAreInRange(long sequence)
            {
                while ((sequence - Util.GetMinimumSequence(_consumers)) >= _ringBuffer._entries.Length)
                {
                    Thread.Yield();
                }
            }
        }
    }
}