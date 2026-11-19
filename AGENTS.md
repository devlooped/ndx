# Implementation notes

`ndx -i|--install` runs a tool from the user's global .NET tool installation.
It reads the installed version with `dotnet tool list --global --format json`,
compares it with the requested package version, and uses `dotnet tool install`
or `dotnet tool update --global` only when needed. The launched entry point is
resolved from the global tool `.store`, including RID-specific package layouts;
it must not be resolved from ndx's NuGet package cache.

`dotnet pack` without `-r` produces only the `ndx` pointer package: `ndx.csproj`
sets `CreateRidSpecificToolPackages=false` for that evaluation, because the
.NET 11 SDK otherwise also packs the AOT RID packages the host can build. Each
RID package is packed by its own `dotnet pack -r <rid>` in the workflows.
