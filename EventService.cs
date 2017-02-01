using System;
using System.Globalization;

namespace AlarmTester
{
    /// <summary>
    /// Coordinates the generation and execution of events - alarms or warnings
    /// </summary>
    /// <seealso cref="System.IDisposable" />
    internal sealed class EventService: IDisposable
    {
        /// <summary>
        /// The event generation service - queues actions to be executed
        /// </summary>
        private readonly EventGenerator _eventService;

        /// <summary>
        /// The task execution service - executes actions against db (SPs)
        /// </summary>
        private readonly TaskProcessor _taskService;

        /// <summary>
        /// Initializes a new instance of the <see cref="EventService"/> class.
        /// </summary>
        /// <param name="frequency">The frequency in events per day</param>
        /// <param name="percentAlarm">The percentage of events to be alarms</param>
        internal EventService(int frequency, int percentAlarm)
        {
            var averageEventsPerSecond = (frequency / 86400.0) * 2;
            var averageDelayMs = (1000.0 / averageEventsPerSecond);
            Console.Write("Frequency -> {0} Events per Second\n\r", averageEventsPerSecond.ToString(CultureInfo.CurrentCulture));
            Console.Write("Average Delay -> {0} ms\n\r", averageDelayMs.ToString(CultureInfo.CurrentCulture));
           
            this._eventService = new EventGenerator(averageDelayMs, percentAlarm);
            this._taskService = new TaskProcessor(averageDelayMs);

            this._eventService.QueueActionForProcessing += this._taskService.QueueNextAction;
        }

        /// <summary>
        /// Generates the events.
        /// </summary>
        internal void GenerateEvents()
        {
            this._eventService.DoWork();
        }

        /// <summary>
        /// Processes the events.
        /// </summary>
        internal void ProcessEvents()
        {
            this._taskService.DoWork();
        }

        /// <summary>
        /// Stops the services
        /// </summary>
        internal void RequestStop()
        {
            this._eventService.StopProcessing();
            this._taskService.StopProcessing();
        }

        /// <summary>
        /// Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources.
        /// </summary>
        public void Dispose()
        {
            this._taskService?.Dispose();
        }
    }
}