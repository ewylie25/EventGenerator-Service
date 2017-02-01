using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Configuration;
using System.Data;
using System.Data.OleDb;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace AlarmTester
{
    /// <summary>
    /// Class responsible for all database related operations.
    /// There are both static and instance based resources - read carefully 
    /// </summary>
    internal sealed class EventRepository
    {
        /// <summary>
        /// The connection string from the configuration file
        /// </summary>
        private static readonly string ConnectionString;

        /// <summary>
        /// The threshold type ids that correspond to alarm events
        /// </summary>
        private readonly List<int> _alarmTypes = new List<int> { 1, 2, 4, 5 };

        /// <summary>
        /// The threshold type ids that correspond to warning events
        /// </summary>
        private readonly List<int> _warningTypes = new List<int> { 3, 6 };

        /// <summary>
        /// SQL query for candidate alarm thresholds
        /// </summary>
        private const string Sql =
            @"select AT.AlarmThresholdID, PP.ProjectPointID, P.ProjectID, P.DeployedProfileID, AT.AlarmThresholdTypeID 
                                    from AlarmThresholds AT
                                    inner join ProjectPoints PP on AT.ProjectPointID = PP.ProjectPointID
                                    inner join Projects P on PP.ProjectID = P.ProjectID
                                    where P.DeployedProfileID IS NOT NULL";

        /// <summary>
        /// Occurs when OnSwitchingModes is executed.
        /// </summary>
        public event EventHandler ToggleEventMode;

        /// <summary>
        /// The available thresholds in the database
        /// </summary>
        private static readonly DataTable AvailableThresholds;

        /// <summary>
        /// Gets the total thresholds.
        /// </summary>
        /// <value>
        /// The total thresholds.
        /// </value>
        private static int TotalThresholds => AvailableThresholds.Rows.Count;

        /// <summary>
        /// The random instance used to get the next threshold to raise an event for
        /// </summary>
        private readonly Random _instanceGenerator;


        internal static ConcurrentBag<string> InsertExecutionTimes = new ConcurrentBag<string>();

        internal static ConcurrentBag<string> UpdateExecutionTimes = new ConcurrentBag<string>();

        private static readonly object MLock = new object();

        private static volatile int _errorCount;

        internal static void WriteOutMetrics()
        {
            Console.Write("Writing execution times out to log...\n\r");
            var path = ConfigurationManager.AppSettings["OutDir"];
            if (!InsertExecutionTimes.IsEmpty)
            {
                lock (MLock)
                {
                    File.WriteAllLines($"{path}\\insertTimes_{DateTime.Now.ToString("yyyy-dd-M--HH-mm-ss")}.txt",
                        InsertExecutionTimes);
                    InsertExecutionTimes = new ConcurrentBag<string>();
                }
            }
            if (!UpdateExecutionTimes.IsEmpty)
            {
                lock (MLock)
                {
                    File.WriteAllLines($"{path}\\updateTimes_{DateTime.Now.ToString("yyyy-dd-M--HH-mm-ss")}.txt",
                        UpdateExecutionTimes);
                    UpdateExecutionTimes = new ConcurrentBag<string>();
                }
            }

            Console.Write("Un-handled Error count: {0}\n\r", _errorCount);
        }

        /// <summary>
        /// Thread safe dictionary that keeps track of the open events - alarms and warnings
        /// events are added when successfully opened and removed when queued for closing 
        /// </summary>
        private static readonly ConcurrentDictionary<int, int> OpenAlarms;

        /// <summary>
        /// Gets the maximum key value in the dictionary of open events
        /// </summary>
        /// <value>
        /// The maximum key.
        /// </value>
        private static volatile int _maxKey;

        /// <summary>
        /// Raises the <see cref="E:SwitchingModes" /> event.
        /// </summary>
        /// <param name="e">The <see cref="EventArgs"/> instance containing the event data.</param>
        private void OnSwitchingModes(EventArgs e)
        {
            var handler = this.ToggleEventMode;
            handler?.Invoke(this, e);
        }

        /// <summary>
        /// Initializes the <see cref="EventRepository"/> class.
        /// Loads the available thresholds in the database
        /// </summary>
        static EventRepository()
        {
            ConnectionString = ConfigurationManager.ConnectionStrings["database"].ConnectionString;
            AvailableThresholds = new DataTable();
            using (OleDbConnection conn = new OleDbConnection(ConnectionString))
            {
                OleDbCommand cmd = new OleDbCommand(Sql, conn);
                conn.Open();
                OleDbDataAdapter adapter = new OleDbDataAdapter(cmd);
                adapter.Fill(AvailableThresholds);
            }
            OpenAlarms = new ConcurrentDictionary<int, int>();
            Console.Write("Loading {0} thresholds \n\r", TotalThresholds);
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="EventRepository"/> class.
        /// </summary>
        internal EventRepository()
        {
            this._instanceGenerator = new Random();
        }

        /// <summary>
        /// Opens the next event.
        /// </summary>
        /// <param name="sender">The sender.</param>
        /// <param name="e">The e.</param>
        internal void OpenNextEvent(object sender, AlarmEventArg e)
        {
            // un-box the sender
            EventGenerator currentGenerator = sender as EventGenerator;

            // get the next threshold and type - don't open events already open
            int next = this._instanceGenerator.Next(TotalThresholds);
            while (OpenAlarms.ContainsKey(next))
            {

                next = this._instanceGenerator.Next(TotalThresholds);

            }
            if (e.EventType == EventGenerator.EventType.Alarm)
            {
                while (!this.IsAlarm(next))
                {
                    next = this._instanceGenerator.Next(TotalThresholds);
                }
            }
            else
            {
                while (!this.IsWarning(next))
                {
                    next = this._instanceGenerator.Next(TotalThresholds);
                }
            }

            // queue the action
            currentGenerator?.OnActionQueued(new QueuedEventArg()
            {
                QueuedAction = () => { OpenAlarm(next, e.EventDateTime); }
            });

            // toggle the mode - opening or closing if we have more than 25 open events
            if (OpenAlarms.Count > 25)
            {
                this.OnSwitchingModes(EventArgs.Empty);
            }
        }

        /// <summary>
        /// Closes the next event.
        /// </summary>
        /// <param name="sender">The sender.</param>
        /// <param name="e">The e.</param>
        internal void CloseNextEvent(object sender, AlarmEventArg e)
        {
            //un-box the sender
            EventGenerator currentGenerator = sender as EventGenerator;

            // find the next event to close
            int next = this._instanceGenerator.Next(_maxKey);
            while (!OpenAlarms.ContainsKey(next))
            {
                next = this._instanceGenerator.Next(_maxKey);
            }

            // remove from the collection of open events
            int value;
            OpenAlarms.TryRemove(next, out value);

            // queue the action
            currentGenerator?.OnActionQueued(new QueuedEventArg()
            {
                QueuedAction = () => { CloseAlarm(value, e.EventDateTime); }
            });

            //switch back to opening events
            this.OnSwitchingModes(EventArgs.Empty);
        }

        /// <summary>
        /// Determines whether the specified row index is an alarm threshold.
        /// </summary>
        /// <param name="rowIndex">Index of the row.</param>
        /// <returns></returns>
        private bool IsAlarm(int rowIndex)
        {
            var dr = AvailableThresholds.Rows[rowIndex];
            var type = Convert.ToInt32(dr[4]);
            return this._alarmTypes.Contains(type);
        }

        /// <summary>
        /// Determines whether the specified row index is a warning threshold.
        /// </summary>
        /// <param name="rowIndex">Index of the row.</param>
        /// <returns></returns>
        private bool IsWarning(int rowIndex)
        {
            var dr = AvailableThresholds.Rows[rowIndex];
            var type = Convert.ToInt32(dr[4]);
            return this._warningTypes.Contains(type);
        }

        /// <summary>
        /// Executes the Insert Stored Procedure
        /// </summary>
        /// <param name="rowIndex">Index of the row in the data table</param>
        /// <param name="time">The time the event was opened</param>
        private static void OpenAlarm(int rowIndex, DateTime time)
        {
            //get the row from the data table
            DataRow dr = AvailableThresholds.Rows[rowIndex];
            var timer = new Stopwatch();
            // first column is the alarm threshold id
            int alarmThresholdId = Convert.ToInt32(dr[0]);
            int newRecordId = 0;
            try
            {
                timer.Start();
                using (OleDbConnection connect = new OleDbConnection(ConnectionString))
                using (OleDbCommand cmd = GetInsertCommand())
                {
                    connect.Open();
                    cmd.Connection = connect;

                    cmd.Parameters["@AlarmThresholdID"].Value = alarmThresholdId;
                    // second column is the project point id
                    cmd.Parameters["@ProjectPointID"].Value = dr[1];
                    // fourth column is the profile id
                    cmd.Parameters["@ProfileID"].Value = dr[3];
                    // third column is the project id
                    cmd.Parameters["@ProjectID"].Value = dr[2];
                    // set the time we're opening the event
                    cmd.Parameters["@AlarmStart"].Value = time;

                    //dummy values since this isn't a real event
                    cmd.Parameters["@EventTriggerValue"].Value = 1;
                    cmd.Parameters["@AlarmDetails"].Value = "Dummy Event";
                    cmd.Parameters["@CopyThresholdValue"].Value = 1;
                    cmd.Parameters["@CopyMinEvents"].Value = 1;
                    cmd.Parameters["@CopyMaxEvents"].Value = 1;

                    // fifth column is the alarm threshold type id
                    cmd.Parameters["@CopyAlarmThreshTypeID"].Value = dr[4];

                    // dummy value
                    cmd.Parameters["@AlarmCriticality"].Value = 1;

                    int rowsAffected = cmd.ExecuteNonQuery();

                    if (rowsAffected > 0)
                    {
                        newRecordId = (int) cmd.Parameters["@RETURN_VALUE"].Value;
                    }
                    else
                    {
                        Console.Write("There were no records affected by the InsertAlarmEventDetail() function.");
                    }
                }
                
                // add the open event to the open event dictionary
                OpenAlarms.TryAdd(alarmThresholdId, newRecordId);
                if (alarmThresholdId > _maxKey)
                {
                    _maxKey = alarmThresholdId;
                }
            }
            catch (Exception ex)
            {
                Console.Write(
                    "The application was unable to insert the new alarm event detail record for id {0}, {1} \n\r",
                    alarmThresholdId, ex);
                _errorCount += 1;

            }
            finally
            {
                timer.Stop();
                InsertExecutionTimes.Add(timer.ElapsedMilliseconds.ToString());
            }
        }

        /// <summary>
        /// Closes the alarm.
        /// </summary>
        /// <param name="id">The alarm event detail identifier.</param>
        /// <param name="time">The time the event was closed</param>
        private static void CloseAlarm(int id, DateTime time)
        {
            var timer = new Stopwatch();
            try
            {

                timer.Start();
                using (OleDbConnection connect = new OleDbConnection(ConnectionString))
                using (OleDbCommand cmd = GetUpdateCommand())
                {
                    connect.Open();
                    cmd.Connection = connect;

                    cmd.Parameters["@AlarmDetailID"].Value = id;
                    cmd.Parameters["@AlarmEnd"].Value = time;

                    int rowsAffected = cmd.ExecuteNonQuery();

                    if (rowsAffected <= 0)
                    {
                        Console.Write(
                            "There were an invalid number of records affected by the UpdateAlarmEventDetail() function.");
                    }
                }
                
            }
            catch (Exception ex)
            {
                Console.Write(
                    "The application was unable to update the existing alarm event detail record (AlarmDetailID = {0}). Ex: {1} \n\r",
                    id, ex);
                _errorCount += 1;
            }
            finally
            {
                timer.Stop();
                UpdateExecutionTimes.Add(timer.ElapsedMilliseconds.ToString());
            }
        }


        /// <summary>
        /// Gets the insert command
        /// </summary>
        /// <returns></returns>
        private static OleDbCommand GetInsertCommand()
        {
            var cmd = new OleDbCommand
            {
                CommandType = CommandType.StoredProcedure,
                CommandText = "Insert_AlarmEventDetail"
            };

            cmd.Parameters.Add(new OleDbParameter("@RETURN_VALUE", OleDbType.Integer, 2147483647, ParameterDirection.ReturnValue, 10, 255, "AlarmDetailID", DataRowVersion.Current, false, null));
            cmd.Parameters.Add(new OleDbParameter("@AlarmThresholdID", OleDbType.Integer, 2147483647, ParameterDirection.Input, 10, 255, "AlarmThresholdID", DataRowVersion.Current, false, null));
            cmd.Parameters.Add(new OleDbParameter("@ProjectPointID", OleDbType.Integer, 2147483647, ParameterDirection.Input, 10, 255, "ProjectPointID", DataRowVersion.Current, false, null));
            cmd.Parameters.Add(new OleDbParameter("@ProfileID", OleDbType.Integer, 2147483647, ParameterDirection.Input, 10, 255, "ProfileID", DataRowVersion.Current, false, null));
            cmd.Parameters.Add(new OleDbParameter("@ProjectID", OleDbType.Integer, 2147483647, ParameterDirection.Input, 10, 255, "ProjectID", DataRowVersion.Current, false, null));
            cmd.Parameters.Add(new OleDbParameter("@AlarmStart", OleDbType.DBTimeStamp, 2147483647, ParameterDirection.Input, 255, 255, "AlarmStart", DataRowVersion.Current, false, null));
            cmd.Parameters.Add(new OleDbParameter("@EventTriggerValue", OleDbType.Double, 2147483647, ParameterDirection.Input, 15, 255, "EventTriggerValue", DataRowVersion.Current, false, null));
            cmd.Parameters.Add(new OleDbParameter("@AlarmDetails", OleDbType.VarWChar, 2000, ParameterDirection.Input, 255, 255, "AlarmDetails", DataRowVersion.Current, false, null));
            cmd.Parameters.Add(new OleDbParameter("@CopyThresholdValue", OleDbType.Double, 2147483647, ParameterDirection.Input, 15, 255, "CopyThresholdValue", DataRowVersion.Current, false, null));
            cmd.Parameters.Add(new OleDbParameter("@CopyMinEvents", OleDbType.Integer, 2147483647, ParameterDirection.Input, 10, 255, "CopyMinEvents", DataRowVersion.Current, false, null));
            cmd.Parameters.Add(new OleDbParameter("@CopyMaxEvents", OleDbType.Integer, 2147483647, ParameterDirection.Input, 10, 255, "CopyMaxEvents", DataRowVersion.Current, false, null));
            cmd.Parameters.Add(new OleDbParameter("@CopyAlarmThreshTypeID", OleDbType.Integer, 2147483647, ParameterDirection.Input, 10, 255, "CopyAlarmThreshTypeID", DataRowVersion.Current, false, null));
            cmd.Parameters.Add(new OleDbParameter("@AlarmCriticality", OleDbType.Integer, 2147483647, ParameterDirection.Input, 10, 255, "AlarmCriticality", DataRowVersion.Current, false, null));

            return cmd;
        }

        /// <summary>
        /// Gets the update command.
        /// </summary>
        /// <returns></returns>
        private static OleDbCommand GetUpdateCommand()
        {
            var cmd = new OleDbCommand
            {
                CommandType = CommandType.StoredProcedure,
                CommandText = "Update_AlarmEventDetail"
            };

            cmd.Parameters.Add(new OleDbParameter("@AlarmDetailID", OleDbType.Integer, 2147483647, ParameterDirection.Input, 10, 255, "AlarmDetailID", DataRowVersion.Current, false, null));
            cmd.Parameters.Add(new OleDbParameter("@AlarmEnd", OleDbType.DBTimeStamp, 2147483647, ParameterDirection.Input, 255, 255, "AlarmEnd", DataRowVersion.Current, false, null));

            return cmd;
        }
    }

}