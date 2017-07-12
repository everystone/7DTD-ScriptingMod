﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NLua;
using NLua.Exceptions;

namespace ScriptingMod.ScriptEngines
{
    internal class LuaEngine : ScriptEngine
    {
        private static LuaEngine _instance;
        public static LuaEngine Instance => _instance ?? (_instance = new LuaEngine());

        private Lua _lua;
        protected override string CommentPrefix => "--";

        private LuaEngine()
        {
            _lua = new Lua();
            _lua.LoadCLRPackage();
            _lua["print"] = new Action<object[]>(Print);
        }

        public override void ExecuteInline(string script)
        {
            try
            {
                Log.Debug("Starting inline LUA script ...");
                _lua.DoString(script);
                Log.Debug("Inline LUA script ended.");
            }
            catch (LuaScriptException ex)
            {
                Log.Exception(ex);
                Log.Error("Inline LUA script failed.");
            }
            catch (Exception ex)
            {
                Log.Exception(ex);
                Log.Error("Inline LUA script failed.");
            }
        }

        public override void ExecuteFile(string filePath)
        {
            var fileName = FileHelper.GetRelativePath(filePath, Api.CommandsFolder);
            try
            {
                Log.Debug($"Starting LUA script {fileName} ...");
                // We are not using _lua.DoFile(..) because it does not support UTF-8 w/ BOM encoding
                string script = File.ReadAllText(filePath);
                _lua.DoString(script);
                Log.Debug($"LUA script {fileName} ended.");
            }
            catch (LuaScriptException ex)
            {
                Log.Exception(ex);
                Log.Error($"LUA script {fileName} failed.");
            }
            catch (Exception ex)
            {
                Log.Exception(ex);
                Log.Error($"LUA script {fileName} failed.");
            }
        }

        public override void SetValue(string name, object value)
        {
            _lua[name] = value;
        }

        #region Methods exposed in LUA

        private void Print(params object[] values)
        {
            if (values == null || values.Length == 0)
                return;
            string output = values.Select(v => v.ToString()).Aggregate((s, s1) => s + s1);
            Log.Out(output);
        }

        #endregion
    }
}
