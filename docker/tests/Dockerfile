FROM mcr.microsoft.com/dotnet/sdk:10.0
WORKDIR /src

# The build context includes the checked-out submodule at knowledge/, which the solution references
# by relative path. A clone without --recurse-submodules leaves that directory empty and the restore
# below fails on the missing project - loudly, which is the right way for it to fail.
COPY . .

RUN dotnet restore Dragonmind.Agents.sln
CMD ["dotnet","test","Dragonmind.Agents.sln","-c","Release","--no-restore"]
