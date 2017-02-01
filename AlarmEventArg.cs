using System;

namespace AlarmTester
{
    /// <summary>
    /// Event Arguments for opening and closing alarms or warnings.
    /// Wrapper for the date time of the raised event and type of event (warning or alarm) 
    /// </summary>
    /// <seealso cref="System.EventArgs" />
    internal sealed class AlarmEventArg : EventArgs
    {
        /// <summary>
        /// The date time of the event
        /// </summary>
        internal DateTime EventDateTime { get; set; }

        /// <summary>
        /// Gets or sets the type of the event.
        /// </summary>
        /// <value>
        /// The type of the event - alarm or warning.
        /// </value>
        internal EventGenerator.EventType EventType { get; set; }
    }
}