
Local
Use the repo root d:\blazorcode\presanalysis-web.
dotnet restore
dotnet build .\presanalysis-web.sln
dotnet run --project .\presanalysis-web.csproj
Then open the local URL printed by dotnet run and smoke test:
1. Home page loads
2. /load works and CSV upload works
3. Timeline / weekly / monthly / range pages load
4. Excel export from /api/export-range works
If you want a local Release publish folder to inspect:
dotnet publish .\presanalysis-web.csproj -c Release -o .\publish-local
Server Build For mltools
dotnet publish .\presanalysis-web.csproj -c Release -r win-x64 --self-contained true -o .\webapp

What To Copy / Where To Copy
Stop iis 
Copy webapp folder to mltools on C:\inetpub\webapp
Start iis



Git updates

git status
git add .
git commit -m "updated files - minor fixes"
git push