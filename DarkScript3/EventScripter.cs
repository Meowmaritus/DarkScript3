﻿using System.Collections.Generic;
using System;
using System.Linq;
using System.Text;
using SoulsFormats;
using Microsoft.ClearScript.V8;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.ClearScript;
using static SoulsFormats.EMEVD.Instruction;
using static SoulsFormats.EMEVD;

namespace DarkScript3
{
    public class EventScripter
    {
        public EMEVD EVD = new EMEVD();


        public EMEDF DOC { get; set; } = new EMEDF();

        private V8ScriptEngine v8 = new V8ScriptEngine();

        public Dictionary<string, (int classIndex, int instrIndex)> Functions = new Dictionary<string, (int classIndex, int instrIndex)>();

        public Dictionary<EMEDF.InstrDoc, List<uint>> FuncBytePositions = new Dictionary<EMEDF.InstrDoc, List<uint>>();

        private List<string> LinkedFiles = new List<string>();

        public EventScripter(string file, string resource = "ds3-common.emedf.json")
        {
            EVD = EMEVD.Read(file);
            InitAll(resource);
        }

        public EventScripter(EMEVD evd = null, string resource = "ds1-common.emedf.json")
        {
            if (evd != null) EVD = evd;
        }

        public void InitAll(string resource)
        {
            v8.AddHostObject("$$$_host", new HostFunctions());
            v8.AddHostObject("EVD", EVD);
            v8.AddHostType("EMEVD", typeof(EMEVD));
            v8.AddHostObject("Scripter", this);
            v8.AddHostType("Object", typeof(object));
            v8.AddHostType("EVENT", typeof(Event));
            v8.AddHostType("INSTRUCTION", typeof(Instruction));
            v8.AddHostType("PARAMETER", typeof(Parameter));
            v8.AddHostType("REST", typeof(Event.RestBehaviorType));
            v8.AddHostType("Console", typeof(Console));

            v8.Execute(File.ReadAllText("script.js"));

            if (resource == null)
            {
                var chooser = new GameChooser();
                chooser.ShowDialog();
                DOC = InitDocsFromResource(chooser.GameDocs);
            }
            else
            {
                DOC = InitDocsFromResource(resource);
            }
        }

        public Instruction MakeInstruction(int bank, int index, uint layer, object[] args)
        {
            Instruction ins = MakeInstruction(bank, index, args);
            ins.Layer = layer;
            return ins;
        }

        public Instruction MakeInstruction(int bank, int index, object[] args)
        {
            EMEDF.InstrDoc doc = DOC[bank][index];
            if (args.Length < doc.Arguments.Length)
                throw new Exception($"Instruction {bank}[{index}] ({doc.Name}) requires {doc.Arguments.Length} arguments.");

            List<object> properArgs = new List<object>();
            if (bank == 2000 && index == 0)
            {
                properArgs.Add(Convert.ToInt32(args[0]));
                properArgs.Add(Convert.ToUInt32(args[1]));
                if (args.Length > 2)
                    for (int i = 2; i < args.Length; i++)
                        properArgs.Add(Convert.ToUInt32(args[i]));
            } else
            {
                for (int i = 0; i < doc.Arguments.Length; i++)
                {
                    EMEDF.ArgDoc argDoc = doc.Arguments[i];
                    Console.WriteLine($"\t\tARG: {args[i]} ({argDoc.Type})");
                    if (argDoc.Type == 0) properArgs.Add(Convert.ToByte(args[i])); //u8
                    else if (argDoc.Type == 1) properArgs.Add(Convert.ToUInt16(args[i])); //u16
                    else if (argDoc.Type == 2) properArgs.Add(Convert.ToUInt32(args[i])); //u32
                    else if (argDoc.Type == 3) properArgs.Add(Convert.ToSByte(args[i])); //s8
                    else if (argDoc.Type == 4) properArgs.Add(Convert.ToInt16(args[i])); //s16
                    else if (argDoc.Type == 5) properArgs.Add(Convert.ToInt32(args[i])); //s32
                    else if (argDoc.Type == 6) properArgs.Add(Convert.ToSingle(args[i])); //f32
                    else if (argDoc.Type == 8) properArgs.Add(Convert.ToUInt32(args[i])); //string position
                    else throw new Exception("Invalid type in argument definition.");
                }
            }
            return new Instruction(bank, index, properArgs);
        }

        private EMEDF InitDocsFromResource(string streamPath)
        {
            EMEDF DOC = EMEDF.Read("Resources\\" + streamPath);
            foreach (EMEDF.EnumDoc enm in DOC.Enums)
            {
                foreach (KeyValuePair<string, string> pair in enm.Values.ToList())
                {
                    string val = enm.Values[pair.Key];
                    enm.Values[pair.Key] = Regex.Replace(val, @"[^\w]", "");
                }
            }

            foreach (EMEDF.ClassDoc bank in DOC.Classes)
            {
                foreach (EMEDF.InstrDoc instr in bank.Instructions)
                {
                    string funcName = UTIL.TitleCaseName(instr.Name);
                    Functions[funcName] = ((int)bank.Index, (int)instr.Index);
                    FuncBytePositions[instr] = GetArgBytePositions(instr.Arguments.Select(i => (ArgType)i.Type).ToList());
                    

                    string argNames = string.Join(", ", instr.Arguments.Select(a => UTIL.CamelCaseName(a.Name.Replace("Class", "Class Name"))));

                    StringBuilder sb = new StringBuilder($"function {funcName} ({argNames}) {{");
                    Console.WriteLine(sb.ToString() + "}");

                    sb.AppendLine($"    return _Instruction({bank.Index}, {instr.Index}, Array.from(arguments));");
                    sb.AppendLine("}");

                    v8.Execute(sb.ToString());
                }
            }
            return DOC;
        }
        private int ByteLengthFromType(long t)
        {
            if (t == 0) return 1; //u8
            if (t == 1) return 2; //u16
            if (t == 2) return 4; //u32
            if (t == 3) return 1; //s8
            if (t == 4) return 2; //s16
            if (t == 5) return 4; //s32
            if (t == 6) return 4; //f32
            if (t == 8) return 4; //string position
            throw new Exception("Invalid type in argument definition.");
        }

        public EMEVD Pack(string code)
        {
            EVD.Events.Clear();
            v8.Execute(code);
            return EVD;
        }

        public string Unpack()
        {
            InitLinkedFiles();

            StringBuilder code = new StringBuilder();
            foreach (var evt in EVD.Events)
            {
                string id = evt.ID.ToString();
                string restBehavior = evt.RestBehavior.ToString();

                Dictionary<Parameter, string> paramNames = ParamNames(evt);
                IEnumerable<string> argNameList = paramNames.Values.Distinct();

                string parameters = string.Join(", ", argNameList);
                string eventHeaderLine = $"Event({id}, {restBehavior}, function() {{";
                code.AppendLine(eventHeaderLine);
                Console.WriteLine(eventHeaderLine);
                for (int insIndex = 0; insIndex < evt.Instructions.Count; insIndex++)
                {
                    Instruction ins = evt.Instructions[insIndex];
                    EMEDF.InstrDoc doc = DOC[ins.Bank][ins.ID];
                    string funcName = UTIL.TitleCaseName(doc.Name);
                    //Console.WriteLine($"\t{funcName}");

                    IEnumerable<ArgType> argStruct = doc.Arguments.Select(arg => (ArgType)arg.Type);
                    string[] args = default;
                    string argString = "";

                    try
                    {

                        if (doc == DOC[2000][0])
                        {
                            argStruct = Enumerable.Repeat(ArgType.Int32, ins.ArgData.Length / 4);
                            args = ins.UnpackArgs(argStruct).Select(a => a.ToString()).ToArray();
                            argString = ArgumentStringInitializer(args, insIndex, paramNames, argStruct);
                        }
                        else
                        {
                            args = ins.UnpackArgs(argStruct).Select(a => a.ToString()).ToArray();
                            argString = ArgumentString(args, ins, insIndex, paramNames);
                        }
                    }
                    catch (Exception ex)
                    {
                        var sb = new StringBuilder();
                        sb.AppendLine($@"Unable to unpack arguments for ""{funcName}""");
                        sb.AppendLine(ex.ToString());
                        throw new Exception(sb.ToString());
                    }
                   
                    string lineOfCode = $"\t{UTIL.TitleCaseName(doc.Name)}({argString});";
                    code.AppendLine(lineOfCode);
                    Console.WriteLine(lineOfCode);
 
                }
                code.AppendLine("});");
                code.AppendLine("");
                Console.WriteLine("});");
            }
            Console.WriteLine($"");
            return code.ToString();
        }

        public void InitLinkedFiles()
        {
            var reader = new BinaryReaderEx(false, EVD.StringData);
            foreach (long offset in EVD.LinkedFileOffsets)
            {
                string linkedFile = reader.GetUTF16(offset);
                LinkedFiles.Add(linkedFile);
            }
        }
        public Dictionary<Parameter, string> ParamNames (Event evt)
        {
            Dictionary<long, List<Parameter>> paramValues = new Dictionary<long, List<Parameter>>();
            for (int i = 0; i < evt.Parameters.Count; i++)
            {
                Parameter prm = evt.Parameters[i];
                if (!paramValues.ContainsKey(prm.SourceStartByte))
                    paramValues[prm.SourceStartByte] = new List<Parameter>();

                paramValues[prm.SourceStartByte].Add(prm);
            }

            Dictionary<Parameter, string> paramNames = new Dictionary<Parameter, string>();

            int ind = 0;
            foreach (var kv in paramValues)
            {
                foreach (var p in kv.Value)
                {
                    paramNames[p] = $"\"X{p.SourceStartByte}:{p.ByteCount}\"";
                }
                ind++;
            }
            return paramNames;
        }

        public string ArgumentStringInitializer(string[] args, int insIndex, Dictionary<Parameter, string> paramNames, IEnumerable<ArgType> argStruct)
        {
            List<uint> positions = GetArgBytePositions(argStruct, 6);
            for (int argIndex = 0; argIndex < argStruct.Count(); argIndex++)
            {
                uint bytePos = positions[argIndex];
                foreach (Parameter prm in paramNames.Keys)
                {
                    if (prm.InstructionIndex == insIndex && bytePos == prm.TargetStartByte)
                    {
                        args[argIndex + 2] = paramNames[prm];
                    }
                }
            }
            return string.Join(", ", args).Trim();
        }

        public string ArgumentString(string[] args, Instruction ins, int insIndex, Dictionary<Parameter, string> paramNames)
        {
            var insDoc = DOC[ins.Bank][ins.ID];

            for (int argIndex = 0; argIndex < args.Count(); argIndex++)
            {
                EMEDF.ArgDoc argDoc = insDoc.Arguments[argIndex];
                uint bytePos = FuncBytePositions[insDoc][argIndex];
                bool isParam = false;

                foreach (Parameter prm in paramNames.Keys)
                {
                    if (prm.InstructionIndex == insIndex && bytePos == prm.TargetStartByte)
                    {
                        isParam = true;
                        args[argIndex] = paramNames[prm];
                    }
                }

                if (!isParam && argDoc.EnumName != null)
                {
                    var enm = DOC.Enums.First(e => e.Name == argDoc.EnumName);
                    args[argIndex] = enm.Values[args[argIndex]];
                }
            }
            if (args.Length > 0) return string.Join(", ", args).Trim();
            return "";
        }

        public List<uint> GetArgBytePositions(IEnumerable<ArgType> argStruct, uint startPos = 0)
        {
            List<uint> positions = new List<uint>();
            uint bytePos = startPos;
            for (int i = 0; i < argStruct.Count(); i++)
            {
                long argType = (long)argStruct.ElementAt(i);
                uint defLength = (uint)ByteLengthFromType(argType);
                if (bytePos % defLength > 0)
                    bytePos += defLength - (bytePos % defLength);

                positions.Add(bytePos);
                bytePos += defLength;
            }
            return positions;
        }

        public static class UTIL
        {
            public static string TitleCaseName(string s)
            {
                if (s == null) return s;

                string[] words = s.Split(' ');
                for (int i = 0; i < words.Length; i++)
                {
                    if (words[i].Length == 0) continue;

                    char firstChar = char.ToUpper(words[i][0]);
                    string rest = "";
                    if (words[i].Length > 1)
                    {
                        rest = words[i].Substring(1);
                    }
                    words[i] = firstChar + rest;
                }
                return Regex.Replace(string.Join("", words), "[^\\w]", "");
            }

            public static string CamelCaseName(string s)
            {
                if (s == null) return s;

                string[] words = s.Split(' ');
                words[0] = char.ToLower(words[0][0]) + words[0].Substring(1);
                for (int i = 1; i < words.Length; i++)
                {
                    if (words[i].Length == 0) continue;

                    char firstChar = char.ToUpper(words[i][0]);
                    string rest = "";
                    if (words[i].Length > 1)
                    {
                        rest = words[i].Substring(1);
                    }
                    words[i] = firstChar + rest;
                }
                return Regex.Replace(string.Join("", words), "[^\\w]", "");
            }

            public static string ParamCode(List<Parameter> parameters)
            {
                if (parameters.Count == 0) return "";
                List<string> pStrings = new List<string>();
                for (int i = 0; i < parameters.Count; i++)
                    pStrings.Add($"ARG{i}");
                return string.Join(", ", pStrings);
            }
        }
    }
}
