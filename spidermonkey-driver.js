var modulePath = scriptArgs[0], outputPath = scriptArgs[1], tier = scriptArgs[2] || "stable";
console.log("> loading module", modulePath);
var bytes = os.file.readFile(modulePath, "binary");
console.log("> compiling module");
var m = new WebAssembly.Module(bytes);
console.log("> extracting code for tier", tier);
var c = wasmExtractCode(m, tier);
console.log("> extracted", c.code.length, "bytes");
var binPath = scriptArgs[1] + ".bin";
console.log("> saving machine code to", binPath);
os.file.writeTypedArrayToFile(binPath, c.code);
var segmentPath = scriptArgs[1] + ".segments.json";
console.log("> saving segment info to", segmentPath);
var segmentJson = JSON.stringify(c.segments, null, '\t');
var prev = os.file.redirect(segmentPath);
print(segmentJson);
os.file.redirect(prev);
