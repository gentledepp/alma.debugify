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
