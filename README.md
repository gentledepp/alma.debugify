# alma.debugify

TLDNR; A dotnet tool that allows you to quickly debug your own nuget packages

Do you maintain several libraries (probably using git..) and use nuget to utilize them in your projects?
Then you may at times want to debug your nuget libraries, but
* Sourcelink and 
* symbol packages (*&st;packageid&gt;.symbols.nupkg*) 
do not work...?

Well, in order to debug your nuget package you could still
- compile your nuget project in `Debug` configuration
- copy the output *.dlls and *.pdbs from *bin\Debug*
- head over to the local nuget package cache at %userprofile%\.nuget\packages
- and replace the dlls of your nuget package with the `Debug` dlls.

Wait... you use git-version to version your nuget packages?
- then you, of course, have to overwrite the &lt;Version&gt; element in your nuget *.cpsroj file first so the versions match up!

Well... that works... sort of.
But later on you need to remember to remove your `Debug` dlls from the package cache again or you run the risk to include them in your final product (in case you ocasionally have to run the publish process on your dev machine... it happens 🙄)


What if there was a tool that could to all this?
Meet ✨debugify✨
- debugify packs and "deploys" your nuget lib including source to the local package cache
- you can specify a single project or a whole solution folder - it picks all potential packable csproj files
- you can also overwrite the version of the packages (`debugify -v 1.8.5` will create a &lt;packageid&gt;.1.8.5.nupkg file)
- and even best, because it creates a marker file `.debugified` into the folder of each debugified package (%userprofile%\.nuget\packages\&lt;packageid&gt;\1.8.5) you can clean it all up by simply calling `debugify cleanup` *anywhere*!

What do you think? 🥳

## Installation

Install debugify as a global dotnet tool:

```bash
dotnet tool install --global alma.debugify
```

## Usage Examples

### Basic Usage

Debugify packages in the current directory:
```bash
debugify
```

### Specify Version with Release Configuration

Debugify a specific version and build in Release mode:
```bash
debugify -v 1.6.6 -c Release
```

### Force Rebuild

Force a full rebuild to ensure fresh DLLs:
```bash
debugify --rebuild
```
or using the short form:
```bash
debugify -r
```

### Specify a Specific Project File

Debugify a specific .csproj file with a version:
```bash
debugify -p ./MyProject.csproj -v 1.6.6
```

### Verbose Output with Rebuild

Get detailed output while forcing a rebuild:
```bash
debugify --verbose --rebuild -c Debug
```

### Override Package ID

When working with projects like Avalonia where multiple projects target the same NuGet package:
```bash
debugify -p ./Avalonia --packageid Avalonia -v 11.0.0
```

This will use "Avalonia" as the package ID instead of inferring it from the project file, allowing you to build multiple projects that contribute to a single NuGet package.

### Cleanup

Remove all debug DLLs from the NuGet cache:
```bash
debugify cleanup
```

### List Debugified Packages

Show all packages that have been debugified:
```bash
debugify list
```

## Command-Line Options

- `-v, --version` - Specify the version you'd like to debugify
- `-c, --configuration` - Build configuration (Debug or Release). Default is Debug
- `-r, --rebuild` - Force a full rebuild of projects (slower but ensures fresh DLLs)
- `-p, --path` - Path to *.csproj file or a folder that contains it
- `--verbose` - Set output to verbose messages
- `--buildargs` - Additional arguments for dotnet build (e.g., " --no-restore")
- `--packageid` - Override the package ID for the NuGet cache lookup. Useful when building projects where the NuGet package name differs from the project's PackageId/AssemblyName (e.g., Avalonia projects where multiple projects contribute to a single package)
