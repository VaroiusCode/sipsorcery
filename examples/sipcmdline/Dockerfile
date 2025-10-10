FROM mcr.microsoft.com/dotnet/runtime:9.0

COPY bin/Debug/net9.0/publish/ App/
WORKDIR /App
ENTRYPOINT ["dotnet", "sipcmdline.dll"]