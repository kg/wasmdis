using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
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

            WasmReader wasmReader;
            using (var stream = new BinaryReader(File.OpenRead(modulePath), System.Text.Encoding.UTF8, false)) {
                wasmReader = new WasmReader(stream);
                wasmReader.Read();
            }

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

            var segmentsPath = Path.Combine(outputDir, "wasm.segments.json");
            var binaryPath = Path.Combine(outputDir, "wasm.bin");

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

                    outputStream.WriteLine($"// {name} @ {offset:X8}  size {count} byte(s)");

                    const int decodeChunkSize = 256;
                    var decodeOffset = offset;
                    do {
                        var insns = dis.Disassemble(compiledBytes, decodeOffset, decodeChunkSize);

                        for (int k = 0; k < insns.Length; k++) {
                            var insn = insns[k];
                            if (insn.Address >= endOffset)
                                break;

                            var operand = insn.Operand;
                            if (
                                insn.HasDetails &&
                                (insn.Details.Operands.Length == 1) &&
                                (insn.Details.Operands[0].Type == X86OperandType.Immediate)
                            ) {
                                operand = MapOffset(insn.Details.Operands[0].Immediate, segments);
                            }

                            outputStream.WriteLine($"{insn.Address:X8}  {insn.Mnemonic} {operand}");
                        }

                        if (insns.Length >= decodeChunkSize) {
                            var lastInsn = insns[insns.Length - 1];
                            var lastAddress = (int)(lastInsn.Address);
                            var nextAddress = lastAddress + lastInsn.Bytes.Length;
                            if (nextAddress >= endOffset)
                                break;
                            else
                                decodeOffset = nextAddress;
                        } else {
                            break;
                        }
                    } while (true);

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
