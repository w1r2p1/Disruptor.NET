using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Disruptor.Test.Support;

namespace Disruptor.Test
{
    [TestFixture]
    public class RingBufferTest
    {
        private RingBuffer<StubEntry> ringBuffer;
        private IConsumerBarrier<StubEntry> consumerBarrier;
        private IProducerBarrier<StubEntry> producerBarrier;

        [SetUp]
        public void setUp()
        {
            ringBuffer = new RingBuffer<StubEntry>(new StubFactory(), 20, new SingleThreadedStrategy(),
                                                   new BusySpinStrategy<StubEntry>());
            consumerBarrier = ringBuffer.CreateConsumerBarrier();
            producerBarrier = ringBuffer.CreateProducerBarrier(new NoOpConsumer(ringBuffer));
        }

        [Test]
        public void ShouldClaimAndGet()
        {
            Assert.AreEqual(-1L, ringBuffer.Cursor);

            var expectedEntry = new StubEntry(2701);

            StubEntry oldEntry = producerBarrier.NextEntry();
            oldEntry.copy(expectedEntry);
            producerBarrier.Commit(oldEntry);

            long sequence = consumerBarrier.WaitFor(0);
            Assert.AreEqual(0, sequence);

            StubEntry entry = ringBuffer.GetEntry(sequence);
            Assert.AreEqual(expectedEntry, entry);

            Assert.AreEqual(0L, ringBuffer.Cursor);
        }

        [Test]
        public void ShouldClaimAndGetWithTimeout()
        {
            Assert.AreEqual(-1L, ringBuffer.Cursor);

            var expectedEntry = new StubEntry(2701);

            StubEntry oldEntry = producerBarrier.NextEntry();
            oldEntry.copy(expectedEntry);
            producerBarrier.Commit(oldEntry);

            long sequence = consumerBarrier.WaitFor(0, 5);
            Assert.AreEqual(0, sequence);

            StubEntry entry = ringBuffer.GetEntry(sequence);
            Assert.AreEqual(expectedEntry, entry);

            Assert.AreEqual(0L, ringBuffer.Cursor);
        }


        [Test]
        public void ShouldGetWithTimeout()
        {
            long sequence = consumerBarrier.WaitFor(0, 5);
            Assert.AreEqual(-1L, sequence);
        }

		[Test]
        public void ShouldClaimAndGetInSeparateThread()
        {
            Task<List<StubEntry>> messages = GetMessages(0, 0);

	        StubEntry expectedEntry = new StubEntry(2701);
	
	        StubEntry oldEntry = producerBarrier.NextEntry();
	        oldEntry.copy(expectedEntry);
	        producerBarrier.Commit(oldEntry);
	
	        Assert.AreEqual(expectedEntry, messages.Result[0]);
        }

        [Test]
        public void ShouldClaimAndGetMultipleMessages()
        {
        	//We need this so that the sequence and values for the entries
        	//aren't the same.  This prevents simple errors mixing up 
        	//.Value and .Sequence
        	int entryValueOffset = 3;
            
        	int numMessages = ringBuffer.Capacity;
            for (int i = 0; i < numMessages; i++)
            {
                StubEntry entry = producerBarrier.NextEntry();
                entry.Value = i + entryValueOffset;
                producerBarrier.Commit(entry);
            }

            int expectedSequence = numMessages - 1;
            long available = consumerBarrier.WaitFor(expectedSequence);
            Assert.AreEqual(expectedSequence, available);

            for (int i = 0; i < numMessages; i++)
            {
                Assert.AreEqual(i + entryValueOffset, ringBuffer.GetEntry(i).Value);
            }
        }

        [Test]
        public void ShouldWrap()
        {
        	//We need this so that the sequence and values for the entries
        	//aren't the same.  This prevents simple errors mixing up 
        	//.Value and .Sequence
        	int entryValueOffset = 3;
            
        	int numMessages = ringBuffer.Capacity;
            int offset = 1000;
            for (int i = 0; i < numMessages + offset; i++)
            {
                StubEntry entry = producerBarrier.NextEntry();
                entry.Value = i + entryValueOffset;
                producerBarrier.Commit(entry);
            }

            int expectedSequence = numMessages + offset - 1;
            long available = consumerBarrier.WaitFor(expectedSequence);
            Assert.AreEqual(expectedSequence, available);

            for (int i = offset; i < numMessages + offset; i++)
            {
                Assert.AreEqual(i + entryValueOffset, ringBuffer.GetEntry(i).Value);
            }
        }

        [Test]
        public void ShouldSetAtSpecificSequence()
        {
            long expectedSequence = 5;
            IForceFillProducerBarrier<StubEntry> forceFillProducerBarrier =
                ringBuffer.CreateForceFillProducerBarrier(new NoOpConsumer(ringBuffer));

            StubEntry expectedEntry = forceFillProducerBarrier.ClaimEntry(expectedSequence);
            expectedEntry.Value = (int) expectedSequence;
            forceFillProducerBarrier.Commit(expectedEntry);

            long sequence = consumerBarrier.WaitFor(expectedSequence);
            Assert.AreEqual(expectedSequence, sequence);

            StubEntry entry = ringBuffer.GetEntry(sequence);
            Assert.AreEqual(expectedEntry, entry);

            Assert.AreEqual(expectedSequence, ringBuffer.Cursor);
        }

	    private Task<List<StubEntry>> GetMessages(long initial, long toWaitFor)
	    {
	        var latch = new AutoResetEvent(false);
	        var consumerBarrier = ringBuffer.CreateConsumerBarrier();
	
	        Task<List<StubEntry>> f = new Task<List<StubEntry>>(
				new TestWaiter(latch, consumerBarrier, initial, toWaitFor).Call);
			f.Start();
	
			Assert.IsTrue(latch.WaitOne(TimeSpan.FromSeconds(1)));
	
	        return f;
	    }
    }

    public class TestWaiter
    {
        private readonly long toWaitForSequence;
        private readonly long initialSequence;
        private AutoResetEvent latch;
        private readonly IConsumerBarrier<StubEntry> consumerBarrier;

        public TestWaiter(AutoResetEvent latch,
                          IConsumerBarrier<StubEntry> consumerBarrier,
                          long initialSequence,
                          long toWaitForSequence)
        {
            this.latch = latch;
            this.initialSequence = initialSequence;
            this.toWaitForSequence = toWaitForSequence;
            this.consumerBarrier = consumerBarrier;
        }


        public List<StubEntry> Call()
        {
            latch.Set();
            consumerBarrier.WaitFor(toWaitForSequence);
			
			List<StubEntry> retval = new List<StubEntry>();
            for (long l = initialSequence; l <= toWaitForSequence; l++)
            {
                retval.Add(consumerBarrier.GetEntry(l));
            }
			return retval;
        }
    }
}