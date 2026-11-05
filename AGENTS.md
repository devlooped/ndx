# Implementation notes

`ndx -i|--install` runs a tool from the user's global .NET tool installation.
It reads the installed version with `dotnet tool list --global --format json`,
compares it with the requested package version, and uses `dotnet tool install`
or `dotnet tool update --global` only when needed. The launched entry point is
resolved from the global tool `.store`, including RID-specific package layouts;
it must not be resolved from ndx's NuGet package cache.
