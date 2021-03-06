﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using JetBrains.Annotations;
using ScriptingMod.Exceptions;
using ScriptingMod.Extensions;
using ScriptingMod.Tools;

namespace ScriptingMod.Commands
{
    [UsedImplicitly]
    public class Repair : ConsoleCmdAbstract
    {

        public override string[] GetCommands()
        {
            return new[] { "dj-repair" };
        }

        public override string GetDescription()
        {
            return "Repairs server problems of various kinds.";
        }

        public override string GetHelp()
        {
            // ----------------------------------(max length: 120 char)----------------------------------------------------------------|
            return $@"
                Scans for server problems of various kinds and tries to repair them. Currently supported scan & repair tasks:
                    {RepairEngine.TasksDict.Aggregate("", (str, kv) => str + $"                    {kv.Key}  =>  {kv.Value}\r\n").Trim()}
                Usage:
                    1. dj-repair [/sim] [/auto] [/interval=<seconds>]
                    2. dj-repair <task letters> [/sim] [/auto] [/interval=<seconds>]
                1. Performs all default repair tasks. Same as ""dj-repair {RepairEngine.TasksDefault}"".
                2. Performs the repair tasks identified by their letter(s).
                Optional parameters:
                    /sim                 Simulate scan and report results without actually repairing anything
                    /auto                Turn automatic repairing in background on or off. See logfile for ongoing repair results.
                    /interval=<seconds>  Interval for how often automatic repairing should occur. Default: 600 (every 10 minutes)
                Examples:
                    dj-repair                          Perform the default repair tasks now.
                    dj-repair p /sim                   Scan for corrupt powerblocks but don't repair anything.
                    dj-repair dr /auto /interval=300   Repair density and respawn every 5 minutes.
                ".Unindent();
        }

        public override void Execute(List<string> parameters, CommandSenderInfo senderInfo)
        {
            TelemetryTools.CollectEvent("command", "execute", GetCommands()[0]);
            try
            {
                ParseParams(parameters, out var tasks, out bool simulate, out bool auto, out int? timerInterval);
                timerInterval = timerInterval ?? 60 * 10; // every 10 minutes by default

                if (auto)
                {
                    if (!PersistentData.Instance.RepairAuto)
                    {
                        PersistentData.Instance.RepairAuto = true;
                        PersistentData.Instance.RepairTasks = tasks;
                        PersistentData.Instance.RepairSimulate = simulate;
                        PersistentData.Instance.RepairInterval = timerInterval.Value; // every 10 minutes by default;
                        PersistentData.Instance.Save();
                        RepairEngine.AutoOn();
                    }
                    else
                    {
                        RepairEngine.AutoOff();
                        PersistentData.Instance.RepairAuto = false;
                        PersistentData.Instance.RepairCounter = 0;
                        PersistentData.Instance.Save();
                    }
                }
                else
                {
                    ThreadManager.AddSingleTask(delegate
                    {
                        try
                        {
                            var repairEngine = new RepairEngine(tasks, simulate, senderInfo);
                            repairEngine.Start();
                        }
                        catch (Exception ex)
                        {
                            Log.Exception(ex);
                            SdtdConsole.Instance.OutputAsync(senderInfo, string.Format(Resources.ErrorDuringCommand, ex.Message));
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                CommandTools.HandleCommandException(ex);
            }
        }

        private static void ParseParams(List<string> parameters, out string tasks, out bool simulate, out bool auto, out int? timerInterval)
        {
            simulate = parameters.Remove("/sim");
            auto = parameters.Remove("/auto");
            timerInterval = CommandTools.ParseOptionAsInt(parameters, "/interval", true);

            if (!auto && timerInterval != null)
                throw new FriendlyMessageException("Setting an interval without turning on automatic repair is useless. Please add the \"/auto\" option.");

            switch (parameters.Count)
            {
                case 0:
                    tasks = RepairEngine.TasksDefault;
                    break;

                case 1:
                    tasks = "";
                    string letters = parameters[0];
                    foreach (var key in RepairEngine.TasksDict.Keys)
                    {
                        if (letters.Contains(key))
                        {
                            tasks += key;
                            letters = letters.Replace(key, "");
                        }
                    }
                    if (letters.Length > 0)
                        throw new FriendlyMessageException($"Did not recognize task letter{(letters.Length == 1 ? "" : "s")} '{letters}'. See help.");
                    break;

                default:
                    throw new FriendlyMessageException("Wrong number of parameters. See help.");
            }
        }
    }
}
