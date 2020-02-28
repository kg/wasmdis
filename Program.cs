using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using Gee.External.Capstone.X86;
using Wasm.Model;

namespace WasmDis {
    class Program {
        public static int Main (string[] args) {
            if (args.Length != 2) {
                Console.Error.WriteLine("Usage: WasmDis module.wasm output-directory");
                return 1;
            }

            var modulePath = args[0];
            var outputPath = args[1];
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

            if (!Directory.Exists(outputPath))
                Directory.CreateDirectory(outputPath);

            var driverPath = Path.Combine(appDir, "spidermonkey-driver.js");
            var smArgs = $"\"{driverPath}\" \"{modulePath}\" \"{outputPath}\\temp\"";
            var psi = new ProcessStartInfo(spidermonkeyPath, smArgs) {
                UseShellExecute = false,
                // CreateNoWindow = true
            };

            Console.WriteLine($"{spidermonkeyPath} {smArgs}");
            using (var proc = Process.Start(psi))
                proc.WaitForExit();

            var segmentsPath = Path.Combine(outputPath, "temp.segments.json");
            var binaryPath = Path.Combine(outputPath, "temp.bin");

            if (!File.Exists(binaryPath) || !File.Exists(segmentsPath))
                throw new Exception("Output from driver not found");

            using (var dis = new CapstoneX86Disassembler(X86DisassembleMode.Bit64) {
                EnableInstructionDetails = true,
                DisassembleSyntax = Gee.External.Capstone.DisassembleSyntax.Intel
            }) {
                var bytes = new byte[0];
                var insns = dis.Disassemble(bytes);
                foreach (var insn in insns)
                    Console.WriteLine(insn.Mnemonic, insn.Operand);
            }

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
}
