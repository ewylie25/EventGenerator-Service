using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace AlarmTester
{
    /// <summary>
    /// Responsible for executing stored procedures against the database at a regular interval
    /// Also tracks and logs execution times
    /// </summary>
    /// <seealso cref="System.IDisposable" />
    internal sealed class TaskProcessor:IDisposable
    {
        /// <summary>
        /// The maximum task count to run in parallel
        /// </summary>
        private const int MaxTaskCount = 50;

        /// <summary>
        /// The interval for the thread timer calculated based on the average delay
        /// Will be between 0.5 and 5 seconds
        /// </summary>
        private readonly int _timeOutMs;

        /// <summary>
        /// The thread timer which executes LaunchTrabnscations at a regular interval
        /// </summary>
        private Timer _mainTimer;

        /// <summary>
        /// The total number of transactions executed since the start of the application
        /// </summary>
        private int _transactionCount;

        /// <summary>
        /// thread safe queue where the alarm generator adds actions to be executed and the 
        /// task processor removes them and executes
        /// </summary>
        private readonly ConcurrentQueue<Action> _actionQueue;

        /// <summary>
        /// Initializes a new instance of the <see cref="TaskProcessor"/> class.
        /// </summary>
        /// <param name="averageDelayMs">The average delay between events in ms.</param>
        internal TaskProcessor(double averageDelayMs)
        {
            this._actionQueue = new ConcurrentQueue<Action>();

            var test = Convert.ToInt32(averageDelayMs*4);
            this._timeOutMs = test < 5000 ? (test > 500 ? test : 500) :5000 ;
            Console.WriteLine($"Transaction Thread Timer Interval -> {this._timeOutMs} ms");
        }

        /// <summary>
        /// Executes the timer
        /// </summary>
        internal void DoWork()
        {
            try
            {
                this._mainTimer = new Timer(this.LaunchTransactions, null, 0, this._timeOutMs);
            }
            catch (Exception ex)
            {
                Console.Write("Task Processor - Fatal Error - {0}", ex);
            }
        }

        /// <summary>
        /// Queues the next action from the alarm generator to the _actionQueue
        /// </summary>
        /// <param name="sender">The sender.</param>
        /// <param name="e">The e.</param>
        internal void QueueNextAction(object sender, QueuedEventArg e)
        {
            if (e.QueuedAction != null)
            {
                this._actionQueue.Enqueue(e.QueuedAction);
            }
        }

        /// <summary>
        /// Stops the timer.
        /// </summary>
        internal void StopProcessing()
        {
            this._mainTimer.Dispose();
        }

        /// <summary>
        /// Launches the transactions - executed on the calculated interval by the thread timer
        /// </summary>
        /// <param name="sender">The sender.</param>
        private void LaunchTransactions(object sender)
        {
            if (this._actionQueue.IsEmpty)
            {
                return;
            }

            //log the total if we happen to hit when a remainder is 0
            if (this._transactionCount % 100 == 0)
            {
                Console.WriteLine($"{DateTime.Now.ToString("yyyy - dd - M--HH - mm - ss")} Total Transactions: {this._transactionCount}");
            }
            
            // launch some tasks
            List<Action> pending = new List<Action>();

            int count = 0;
            for (int i = 0; i < this._actionQueue.Count; i++)
            {
                if (i == MaxTaskCount)
                {
                    break;
                }

                Action nextAction;
                if (!this._actionQueue.TryDequeue(out nextAction))
                {
                    Console.WriteLine("Warning - Failed Dequeue");
                    break;
                }

                pending.Add(nextAction);
                count++;
            }
            this._transactionCount += count;
            Parallel.Invoke(pending.ToArray());


            // notify the user if we still have a full queue pending
            if (this._actionQueue.Count > MaxTaskCount)
            {
                Console.WriteLine($"Warning - { this._actionQueue.Count } pending actions in queue");
            }

            if (EventRepository.InsertExecutionTimes.Count > 1000 && EventRepository.UpdateExecutionTimes.Count > 1000)
            {
                EventRepository.WriteOutMetrics();
            }
        }

        /// <summary>
        /// Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources.
        /// </summary>
        public void Dispose()
        {
            this._mainTimer?.Dispose();
        }
    }
}