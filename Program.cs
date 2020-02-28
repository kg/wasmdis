using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Gee.External.Capstone.X86;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Wasm.Model;

namespace WasmDis {
    class Program {
        public static int Main (string[] args) {
            if (args.Length != 2) {
                Console.Error.WriteLine("Usage: WasmDis module.wasm output-directory");
                return 1;
            }

            var modulePath = args[0];
            var outputDir = args[1];
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

            var driverPath = Path.Combine(appDir, "spidermonkey-driver.js");
            var smArgs = $"\"{driverPath}\" \"{modulePath}\" \"{outputDir}\\wasm\"";
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

                        if (seg.funcIndex.HasValue)
                            wasmReader.FunctionNames.TryGetValue(seg.funcIndex.Value, out seg.name);

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
                    var count = (int)(segment.funcBodyEnd.Value - offset);

                    outputStream.WriteLine($"{segment.name ?? "unnamed"} @ {offset:X8}");

                    var insns = dis.Disassemble(compiledBytes, offset, count);
                    foreach (var insn in insns)
                        outputStream.WriteLine(insn.Mnemonic, insn.Operand);

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
