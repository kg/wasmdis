# WasmDis

## Usage
```wasmdis module-file output-directory [regex] [tier]```

where ```module-file``` is the path of a .wasm module, 

```output-directory``` is a directory for storing the output of the tool, 

```regex``` is an optional .NET regular expression used to filter functions, 

and ```tier``` specifies which spidermonkey codegen tier you want. ```ion``` is the high quality background tier which should generate smaller/faster code, but the other tiers matter too.

## Prerequisites
You need to have ```jsvu``` installed and make sure you've pulled down a build of spidermonkey using it so that you have a ```~/.jsvu/sm```.
