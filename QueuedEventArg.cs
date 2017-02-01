using System;

namespace AlarmTester
{
    internal sealed class QueuedEventArg : EventArgs
    {
        
        internal Action QueuedAction { get; set; }
    }
}