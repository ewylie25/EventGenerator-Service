using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace AlarmTester
{
    internal static class Program
    {
        /// <summary>
        /// The event service - opens and closes events based on a provided frequency and 
        /// percent alarms to warnings. 
        /// </summary>
        private static EventService _service;

        /// <summary>
        /// Main Program logic, creates the event service and launches the two main processes
        /// </summary>
        /// <param name="args">command line arguments</param>
        static void Main(string[] args)
        {
            // parse command line arguments
            if (args.Length == 0)
            {
                Console.WriteLine("Please enter the number of events per day.");
                Console.WriteLine("Usage: AlarmTester <frequency> (<percent alarms>)");
                return;
            }

            int frequency;
            if (!int.TryParse(args[0], out frequency))
            {
                Console.WriteLine("Please enter a numeric argument");
                Console.WriteLine("Usage: AlarmTester <frequency> (<percent alarms>)");
                return;
            }

            int percentAlarms = 50;
            if (args.Length > 1)
            {
                if (!int.TryParse(args[1], out percentAlarms))
                {
                    percentAlarms = 50;
                }
            }

            //handle ctrl-c and related events in console
            Console.CancelKeyPress += delegate
            {
                KillEveryoneOnExit();
            };
            _handler = ConsoleEventCallback;
            NativeMethods.SetConsoleCtrlHandler(_handler, true);

            //create service
            _service = new EventService(frequency, percentAlarms); 

            // launch thread to queue open/close alarm/warnings
            var eventCreation = Task.Factory.StartNew(() => _service.GenerateEvents());
            // launch thread to process queue and write to database (separate thread so that poor db performance does not block queuing of events)
            var eventProcessing = Task.Factory.StartNew(() => _service.ProcessEvents());

            //wait until threads are killed/complete
            Task.WaitAll(eventCreation, eventProcessing);
        }

        /// <summary>
        /// The delegate referenced to the callback method
        /// </summary>
        static ConsoleEventDelegate _handler;

        /// <summary>
        /// delegate for ctrl events from console
        /// </summary>
        /// <param name="eventType">Type of the event.</param>
        /// <returns></returns>
        private delegate bool ConsoleEventDelegate(int eventType);



        /// <summary>
        /// Stops the processes if running.
        /// </summary>
        private static void KillEveryoneOnExit()
        {
            Console.Write("Killing everything... \n\r");
            _service.RequestStop();
            _service.Dispose();

        }

        /// <summary>
        /// Callback for ctrl events from console- kills process on ctrl-c
        /// </summary>
        /// <param name="eventType">Type of the event.</param>
        /// <returns></returns>
        static bool ConsoleEventCallback(int eventType)
        {
            if (eventType == 2)
            {
                KillEveryoneOnExit();
            }
            return false;
        }

        /// <summary>
        /// Methods that use Platform Invocation Services (unmanaged code)
        /// Specifically used for capturing Ctrl-c events to kill program
        /// </summary>
        private static class NativeMethods
        {
            /// <summary>
            /// Sets the handler function for ctrl events from console.
            /// </summary>
            /// <param name="callback">The callback.</param>
            /// <param name="add">if set to <c>true</c> [add].</param>
            /// <returns></returns>
            [DllImport("kernel32.dll", SetLastError = true)]
            internal static extern bool SetConsoleCtrlHandler(ConsoleEventDelegate callback, bool add);
        }
    }

}
