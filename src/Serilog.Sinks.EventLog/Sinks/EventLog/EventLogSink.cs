﻿// Copyright 2014 Serilog Contributors
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

using System;
using System.Diagnostics;
using System.IO;
using Serilog.Core;
using Serilog.Debugging;
using Serilog.Events;
using Serilog.Formatting;

namespace Serilog.Sinks.EventLog
{
	/// <summary>
	/// Writes log events as documents to the Windows event log.
	/// </summary>
	/// <remarks>Be aware of changing the source/logname, see: http://stackoverflow.com/questions/804284/how-do-i-write-to-a-custom-windows-event-log?rq=1</remarks>
	public class EventLogSink : ILogEventSink
	{
		readonly ITextFormatter _textFormatter;
	    readonly string _logName;
	    const string APPLICATION_LOG = "Application";
	    readonly System.Diagnostics.EventLog _log;

	    /// <summary>
	    /// Construct a sink posting to the Windows event log, creating the specified <paramref name="source"/> if it does not exist.
	    /// </summary>
	    /// <param name="source">The source name by which the application is registered on the local computer. </param>
	    /// <param name="logName">The name of the log the source's entries are written to. Possible values include Application, System, or a custom event log.</param>
	    /// <param name="textFormatter">Supplies culture-specific formatting information, or null.</param>
	    /// <param name="machineName">The name of the machine hosting the event log written to.</param>
	    /// <param name="manageEventSource">If false does not check/create event source.  Defaults to true i.e. allow sink to manage event source creation</param>
	    public EventLogSink(string source, string logName, ITextFormatter textFormatter, string machineName,
	        bool manageEventSource)
	    {
	        if (source == null) throw new ArgumentNullException(nameof(source));
	        if (textFormatter == null) throw new ArgumentNullException(nameof(textFormatter));

	        //The source is limitted in length and allowed chars
	        //see: https://msdn.microsoft.com/en-us/library/e29k5ebc%28v=vs.110%29.aspx
	        source = source.Substring(0, Math.Min(source.Length, 212));
	        source = source.Replace("<", "_");
	        source = source.Replace(">", "_");

	        _textFormatter = textFormatter;
	        _logName = logName;

	        if (string.IsNullOrWhiteSpace(_logName))
	            _logName = APPLICATION_LOG;

            _log = machineName == null ?
                new System.Diagnostics.EventLog(_logName) :
                new System.Diagnostics.EventLog(_logName, machineName);

	        if (manageEventSource)
	        {
                var sourceData = new EventSourceCreationData(source, _logName) { MachineName = machineName };
                Action logSourceMoved = () => { };

                var sourceExistsInAnyLog = System.Diagnostics.EventLog.SourceExists(source, machineName);
	            if (sourceExistsInAnyLog)
	            {
	                var existingLogWithSourceName = System.Diagnostics.EventLog.LogNameFromSourceName(source, machineName);

	                if (!string.IsNullOrWhiteSpace(existingLogWithSourceName))
	                {
	                    //remove it from previous log so we can associated it with the current logName
	                    System.Diagnostics.EventLog.DeleteEventSource(source, machineName);

	                    //stash a reference to this guy so we can add a log entry noting the logs your looking for are in the
	                    //log previously associated with "source"
	                    //we don't log here in case creating the event source fails (if the create failed, an entry here would be misleading)
	                    logSourceMoved = () =>
	                    {
	                        var metaSource = $"serilog-{_logName}";
	                        if (!System.Diagnostics.EventLog.SourceExists(metaSource, machineName))
	                            System.Diagnostics.EventLog.CreateEventSource(new EventSourceCreationData(metaSource,
	                                _logName)
	                            {
	                                MachineName = machineName
	                            });

	                        _log.Source = metaSource;
	                        _log.WriteEntry(
	                            message:
	                                $"Event source {source} was previously registered in log {existingLogWithSourceName}. " +
	                                $"The source has been registered with this log, {_logName}, however a computer restart may be required " +
	                                $"before event logs will appear in {_logName} with source {source}. Until then, messages may be logged to {existingLogWithSourceName}.",
	                            type: EventLogEntryType.Warning,
	                            eventID: (int) LogEventLevel.Warning);
	                    };
	                }
	            }
	            else
	            {
                    System.Diagnostics.EventLog.CreateEventSource(sourceData);
                }

	            logSourceMoved();
	        }

            _log.Source = source;
	    }

	    /// <summary>
	    /// Emit the provided log event to the sink.
	    /// </summary>
	    /// <param name="logEvent">The log event to write.</param>
	    /// <remarks>
	    /// <see cref="LogEventLevel.Debug" />, <see cref="LogEventLevel.Information" /> and <see cref="LogEventLevel.Verbose" /> are registered as <see cref="EventLogEntryType.Information" />.
	    /// <see cref="LogEventLevel.Error" />, <see cref="LogEventLevel.Fatal" /> are registered as <see cref="EventLogEntryType.Error" />.
	    /// <see cref="LogEventLevel.Warning" /> are registered as <see cref="EventLogEntryType.Warning" />.
	    /// The Event ID in the Windows log will be set to the integer value of the <paramref name="logEvent"/>'s <see cref="LogEvent.Level"/> property, so that the log can be filtered with more granularity.</remarks>
	    public void Emit(LogEvent logEvent)
	    {
	        if (logEvent == null) throw new ArgumentNullException("logEvent");

	        EventLogEntryType type;
	        switch (logEvent.Level)
	        {
	            case LogEventLevel.Debug:
	            case LogEventLevel.Information:
	            case LogEventLevel.Verbose:
	                type = EventLogEntryType.Information;
	                break;

	            case LogEventLevel.Error:
	            case LogEventLevel.Fatal:
	                type = EventLogEntryType.Error;
	                break;

	            case LogEventLevel.Warning:
	                type = EventLogEntryType.Warning;
	                break;

	            default:
	                SelfLog.WriteLine("Unexpected logging level, writing to EventLog as Information");
	                type = EventLogEntryType.Information;
	                break;
	        }

	        var payloadWriter = new StringWriter();
	        _textFormatter.Format(logEvent, payloadWriter);

	        //The payload is limitted in length and allowed chars
	        //see: https://msdn.microsoft.com/en-us/library/e29k5ebc%28v=vs.110%29.aspx
	        var payload = payloadWriter.ToString();
	        if (payload.Length > 31839)
	        {
	            //trim
	            payload = payload.Substring(0, 31839);
	        }

	        _log.WriteEntry(
	            message: payload,
	            type: type,
                //even though the api is type int, eventID must be between 0 and 65535
                //https://msdn.microsoft.com/en-us/library/d3159s0c(v=vs.110).aspx
                eventID: (ushort)(EventIdHash.Compute(logEvent.MessageTemplate.Text)));
	    }
	}
}
