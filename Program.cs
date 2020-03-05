using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using Gee.External.Capstone.X86;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Wasm.Model;

namespace WasmDis {
    class Program {
        public static int Main (string[] args) {
            if (args.Length < 2) {
                Console.Error.WriteLine("Usage: WasmDis module.wasm output-directory [function-name-regex] [compile-tier]");
                Console.Error.WriteLine("       WasmDis input-directory/module-name output-directory [function-name-regex]");
                Console.Error.WriteLine("       (input-directory must point to a directory containing module-name.bin, module-name.wasm and module-name.segments.json files)");
                Console.Error.WriteLine("  function-name-regex is a .NET Framework regular expression that must match for a function to be disassembled");
                Console.Error.WriteLine("  compile-tier specifies which compilation tier should be used to generate native code from the webassembly module.");
                Console.Error.WriteLine("  valid tiers: 'stable', 'best', 'baseline', or 'ion'; the default is 'stable'.");
                return 1;
            }

            var modulePath = args[0];
            var outputDir = args[1];
            Regex functionNameRegex = null;
            if ((args.Length > 2) && !string.IsNullOrWhiteSpace(args[2]))
                functionNameRegex = new Regex(args[2], RegexOptions.Compiled | RegexOptions.IgnoreCase);
            Console.WriteLine($"Analyzing module {modulePath}...");

            string segmentsPath, binaryPath;
            bool usingExistingBinary = false;

            BinaryReader wasmStream;
            if (File.Exists(modulePath)) {
                segmentsPath = Path.Combine(outputDir, "wasm.segments.json");
                binaryPath = Path.Combine(outputDir, "wasm.bin");
            } else {
                usingExistingBinary = true;
                segmentsPath = modulePath + ".segments.json";
                binaryPath = modulePath + ".bin";
                modulePath = modulePath + ".wasm";

                if (!File.Exists(binaryPath))
                    throw new Exception("Binary not found: " + binaryPath);
                if (!File.Exists(segmentsPath))
                    throw new Exception("Segments not found: " + segmentsPath);
            }

            if (!File.Exists(modulePath))
                throw new Exception("Wasm module not found: " + modulePath);
            wasmStream = new BinaryReader(File.OpenRead(modulePath), System.Text.Encoding.UTF8, false);

            WasmReader wasmReader;
            using (wasmStream) {
                wasmReader = new WasmReader(wasmStream);
                wasmReader.Read();
            }

            if (!usingExistingBinary) {
                var appDir = Path.GetDirectoryName(GetPathOfAssembly(typeof(Program).Assembly));

                Console.WriteLine($"Compiling module in SpiderMonkey...");

                var jsvuDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".jsvu");
                if (!Directory.Exists(jsvuDir))
                    throw new Exception("jsvu not found at " + jsvuDir);

                var smNames = new[] { "sm.cmd", "sm" };
                string spidermonkeyPath = null;
                foreach (var smName in smNames) {
                    var testPath = Path.Combine(jsvuDir, smName);
                    if (File.Exists(testPath))
                        spidermonkeyPath = testPath;
                }

                if (spidermonkeyPath == null)
                    throw new Exception("Spidermonkey not found in .jsvu directory");

                if (!Directory.Exists(outputDir))
                    Directory.CreateDirectory(outputDir);

                var compileTier = args.Length > 3 ? args[3] : "stable";

                var driverPath = Path.Combine(appDir, "spidermonkey-driver.js");
                var smArgs = $"\"{driverPath}\" \"{modulePath}\" \"{outputDir}\\wasm\" {compileTier}";
                var psi = new ProcessStartInfo(spidermonkeyPath, smArgs) {
                    UseShellExecute = false,
                    // CreateNoWindow = true
                };

                Console.WriteLine($"{spidermonkeyPath} {smArgs}");
                using (var proc = Process.Start(psi))
                    proc.WaitForExit();
            }

            if (!File.Exists(binaryPath) || !File.Exists(segmentsPath))
                throw new Exception("Output from driver not found");

            Console.WriteLine("Processing segment data...");

            var segments = new List<WasmSegment>();

            using (var streamReader = new StreamReader(segmentsPath)) {
                var jsonReader = new JsonTextReader(streamReader);
                while (jsonReader.Read()) {
                    if (jsonReader.TokenType == JsonToken.StartObject) {
                        var jo = JObject.Load(jsonReader);
                        var seg = jo.ToObject<WasmSegment>();

                        if (seg.funcIndex.HasValue) {
                            if (!wasmReader.FunctionNames.TryGetValue(seg.funcIndex.Value, out seg.name))
                                seg.name = $"unnamed{seg.funcIndex.Value:X4}";
                        }

                        segments.Add(seg);
                    }
                }
            }

            Console.WriteLine("Performing disassembly...");

            var compiledBytes = File.ReadAllBytes(binaryPath);
            var outputPath = Path.Combine(outputDir, "disassembly.txt");

            Console.Write("...");

            using (var outputStream = new StreamWriter(outputPath, false, Encoding.UTF8))
            using (var dis = new CapstoneX86Disassembler(X86DisassembleMode.Bit64) {
                EnableInstructionDetails = true,
                DisassembleSyntax = Gee.External.Capstone.DisassembleSyntax.Intel
            }) {
                var importCount = wasmReader.Imports.entries.Count(imp => imp.kind == external_kind.Function);

                byte[] tempBuf = new byte[segments.Max(s => s.end - s.begin) + 1];
                for (int i = 0; i < segments.Count; i++) {
                    var segment = segments[i];
                    if (!segment.funcBodyBegin.HasValue)
                        continue;

                    if (i % 5 == 0) {
                        Console.CursorLeft = 0;
                        Console.Write($" {i} / {segments.Count} ...      ");
                    }

                    var offset = segment.funcBodyBegin.Value;
                    var endOffset = segment.funcBodyEnd.Value;
                    var count = (int)(endOffset - offset);
                    var name = segment.name;

                    if (functionNameRegex != null && !functionNameRegex.IsMatch(name))
                        continue;

                    outputStream.WriteLine($"// {name}#{segment.funcIndex} @ {offset:X8}");
                    outputStream.WriteLine($"//    native size {count} byte(s)");

                    var bodyIndex = segment.funcIndex.Value - importCount;
                    // FIXME: Without the -importCount offset the function body for interp_exec_method is obviously wrong,
                    //  but I'm not convinced this is right either. We should always have a valid index here.
                    if (bodyIndex >= 0) {
                        var wasmBody = wasmReader.Code.bodies[bodyIndex];
                        outputStream.WriteLine($"//    wasm size {wasmBody.body_size} byte(s)");
                        if (wasmBody.locals.Length > 0) {
                            outputStream.WriteLine($"//    wasm locals:");
                            foreach (var le in wasmBody.locals)
                                outputStream.WriteLine($"//      {le.type} x{le.count}");
                        }
                    }

                    bool wasDead = false;

                    // fixme: capstone is broken as hell why did I even expect a nuget library to work
                    Array.Clear(tempBuf, 0, tempBuf.Length);
                    Array.Copy(compiledBytes, offset, tempBuf, 0, count);
                    
                    foreach (var insn in dis.Iterate(tempBuf, offset)) {
//                        var relativeAddress = insn.Address;
                        var absoluteAddress = insn.Address;
                        if (absoluteAddress >= endOffset)
                            break;
                        if ((absoluteAddress + insn.Bytes.Length) > endOffset)
                            break;

                        // HACK: Strip dead instructions from the output because they're meaningless padding
                        var isDead = (insn.Mnemonic == "hlt");
                        if (isDead) {
                            if (!wasDead)
                                outputStream.WriteLine("...");
                            wasDead = isDead;
                            continue;
                        } else {
                            wasDead = false;
                        }

                        // If an instruction's operand is an address assign it a meaningful name+offset label if possible by
                        //  looking it up in our table of code segments
                        var operand = insn.Operand;
                        if (
                            insn.HasDetails &&
                            (insn.Details.Operands.Length == 1) &&
                            (insn.Details.Operands[0].Type == X86OperandType.Immediate)
                        ) {
                            operand = MapOffset(insn.Details.Operands[0].Immediate, segments);
                        }

                        outputStream.WriteLine($"{absoluteAddress:X8}  {insn.Mnemonic} {operand}");
                    }

                    outputStream.WriteLine($"// end of {name} @ {segment.funcBodyEnd.Value:X8}");
                    outputStream.WriteLine();
                }
            }

            Console.CursorLeft = 0;

            Console.WriteLine($"Complete. Results written to {outputPath}.");

            if (Debugger.IsAttached) {
                Console.WriteLine("Press enter to exit");
                Console.ReadLine();
            }

            return 0;
        }

        static string MapOffset (long immediate, List<WasmSegment> segments) {
            foreach (var seg in segments) {
                if (!seg.funcBodyBegin.HasValue)
                    continue;

                if ((immediate < seg.funcBodyBegin.Value) || (immediate > seg.funcBodyEnd.Value))
                    continue;

                var offset = immediate - seg.funcBodyBegin.Value;
                return $"0x{immediate:X8} ({seg.name} + 0x{offset:X4})";
            }

            return $"0x{immediate:X8}";
        }

        public static void Assert (
            bool b,
            string description = null,
            [CallerMemberName] string memberName = "",
            [CallerFilePath]   string sourceFilePath = "",
            [CallerLineNumber] int sourceLineNumber = 0
        ) {
            if (!b)
                throw new Exception(string.Format(
                    "{0} failed in {1} @ {2}:{3}",
                    description ?? "Assert",
                    memberName, Path.GetFileName(sourceFilePath), sourceLineNumber
                ));
        }

        public static string GetPathOfAssembly (Assembly assembly) {
            var uri = new Uri(assembly.CodeBase);
            var result = Uri.UnescapeDataString(uri.AbsolutePath);

            if (String.IsNullOrWhiteSpace(result))
                result = assembly.Location;

            result = result.Replace('/', System.IO.Path.DirectorySeparatorChar);

            return result;
        }
    }

    public struct WasmSegment {
        public uint kind;
        public long begin, end;

        public uint? funcIndex;
        public long? funcBodyBegin, funcBodyEnd;

        public string name;
    }
}
