using System;
using System.Configuration;
using System.IO;
using System.Threading;

namespace AlarmTester
{
    /// <summary>
    /// Generates events - alarms or warnings based on the specified frequency and ratio of
    /// warnings to alarms. The events are queued for the main thread to process in parallel.
    /// 
    /// </summary>
    internal sealed class EventGenerator
    {
        /// <summary>
        /// Indicates if generator is running or not.
        /// Setting to false will stop the event generation
        /// </summary>
        private volatile bool _running;

        /// <summary>
        /// The mode of the generator - either opening or closing open events
        /// </summary>
        private Mode _currentMode = Mode.Open;

        /// <summary>
        /// relative standard deviation
        /// </summary>
        private const double Rsd = 0.2;

        // standard deviation used in delay calculation
        private readonly double _stdDev;

        /// <summary>
        /// The average delay in ms
        /// </summary>
        private readonly double _averageDelayInMs;

        /// <summary>
        /// percentage of events that are alarms
        /// </summary>
        private readonly double _statisticalAlarmCutOff;

        /// <summary>
        /// Instance of random used to calculate the delay between events
        /// </summary>
        private readonly Random _instanceEventGenerator;

        /// <summary>
        /// Instance of random used to decide if the next event is an alarm or warning
        /// </summary>
        private readonly Random _instanceAlarmGenerator;

        /// <summary>
        /// The event type - either alarm or warning
        /// </summary>
        internal enum EventType
        {
            Warning = 0,
            Alarm = 1
        }

        /// <summary>
        /// Mode - denotes if we're currently opening or closing events
        /// </summary>
        private enum Mode
        {
            Open = 0,
            Close = 1
        }

        /// <summary>
        /// Toggle which mode we're in - opening or closing
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void ToggleMode(object sender, EventArgs e)
        {
            this._currentMode = this._currentMode == Mode.Open ? Mode.Close : Mode.Open;
        }

        /// <summary>
        /// stop the generation process
        /// </summary>
        internal void StopProcessing()
        {
            this._running = false;
            EventRepository.WriteOutMetrics();
        }

        /// <summary>
        /// Event handler for creating an open event action on the queue
        /// </summary>
        public event EventHandler<AlarmEventArg> AlarmOpenEventRaised;

        /// <summary>
        /// Event handler for creating an closed event action on the queue
        /// </summary>
        public event EventHandler<AlarmEventArg> AlarmCloseEventRaised;

        /// <summary>
        ///  Event handler for queuing events in the TaskProcessor
        /// </summary>
        public event EventHandler<QueuedEventArg> QueueActionForProcessing;

        private void OnAlarmOpened(AlarmEventArg e)
        {
            var handler = this.AlarmOpenEventRaised;
            handler?.Invoke(this, e);
        }

        private void OnAlarmClosed(AlarmEventArg e)
        {
            var handler = this.AlarmCloseEventRaised;
            handler?.Invoke(this, e);
        }

        internal void OnActionQueued(QueuedEventArg e)
        {
            var handler = this.QueueActionForProcessing;
            handler?.Invoke(this, e);
        }


        /// <summary>
        /// Initializes a new instance of the <see cref="EventGenerator"/> class.
        /// </summary>
        /// <param name="averageDelayMs">The average delay between events</param>
        /// <param name="percentAlarms">The percent alarms expected as percentage (int)</param>
        internal EventGenerator(double averageDelayMs, int percentAlarms)
        {
            this._instanceEventGenerator = new Random();
            this._instanceAlarmGenerator = new Random();

            this._averageDelayInMs = averageDelayMs;
            this._stdDev = Rsd*averageDelayMs;

            Console.Write("Standard Deviation of Delay -> {0} ms\n\r", this._stdDev);

            this._statisticalAlarmCutOff = percentAlarms/100.0;

            var dataWrapper = new EventRepository();
            
            //set up our event handlers
            this.AlarmOpenEventRaised += dataWrapper.OpenNextEvent;
            this.AlarmCloseEventRaised += dataWrapper.CloseNextEvent;
            dataWrapper.ToggleEventMode += this.ToggleMode;
        }

        /// <summary>
        /// Method used to determine how long you would wait for the event to fire next.
        /// </summary>
        /// <returns>The number of milliseconds to wait for the next event to fire.</returns>
        private double GetNextDelay()
        {
            double u1 = this._instanceEventGenerator.NextDouble();
            double u2 = this._instanceEventGenerator.NextDouble();
            double randStdNormal = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(2.0 * Math.PI * u2);
            double randNormal = this._averageDelayInMs + this._stdDev * randStdNormal; 
            return randNormal;
        }

        /// <summary>
        /// Method used to determine the next type of event to fire
        /// </summary>
        /// <returns>the event type</returns>
        private EventType GetNextEventType()
        {
            var next = this._instanceAlarmGenerator.NextDouble();
            return next <= this._statisticalAlarmCutOff ? EventType.Alarm : EventType.Warning;
        }

        /// <summary>
        ///  Does the work - generates events 
        /// </summary>
        internal void DoWork()
        {
            try
            {
                this._running = true;

                //create the wait handle for the timer
                var autoEvent = new AutoResetEvent(false);

                //trigger the thread timer to execute immediately
                var timer = new Timer(this.CreateEvent, autoEvent, 0, Timeout.Infinite);

                //exit if the flag gets flipped
                while (this._running)
                {
                    //wait to be signaled that the callback was executed
                    autoEvent.WaitOne();

                    //set the timer for the next delay interval
                    var delayMs = Convert.ToInt32(this.GetNextDelay());
                    timer.Change(delayMs, Timeout.Infinite);
                }
            }
            catch (Exception ex)
            {
                Console.Write("Event Generator - Fatal Error - {0}", ex);
            }

        }

        /// <summary>
        /// Callback for creating events at an interval
        /// </summary>
        /// <param name="sender">AutoResetEvent</param>
        private void CreateEvent(object sender)
        {
            //un-box the wait handle
            var autoEvent = (AutoResetEvent) sender;
            
            //create the event
            if (this._currentMode == Mode.Open)
            {
                var myEvent = this.GetNextEventType();
                this.OnAlarmOpened(new AlarmEventArg
                {
                    EventDateTime = DateTime.UtcNow,
                    EventType = myEvent
                });
            }
            if (this._currentMode == Mode.Close)
            {
                this.OnAlarmClosed(new AlarmEventArg
                {
                    EventDateTime = DateTime.UtcNow
                });
            }

            //signal the timer the callback is complete
            autoEvent.Set();
        }
    }
}