#if INTERACTIVE
#r "nuget: FAKE.Core"
#r "nuget: Fake.Core.Target"
#r "nuget: Fake.IO.FileSystem"
#r "nuget: Fake.Tools.Git"
#r "nuget: Fake.DotNet.Cli"
#r "nuget: Fake.DotNet.AssemblyInfoFile"
#r "nuget: Fake.DotNet.Paket"
#r "nuget: Fake.JavaScript.Npm"
#r "nuget: Paket.Core"
#else
#r "paket:
nuget FSharp.Core 5.0.0
nuget FAKE.Core
nuget Fake.Core.Target
nuget Fake.IO.FileSystem
nuget Fake.Tools.Git
nuget Fake.DotNet.Cli
nuget Fake.DotNet.AssemblyInfoFile
nuget Fake.DotNet.Paket
nuget Fake.JavaScript.Npm
nuget Paket.Core prerelease //"
#endif

#load "paket-files/wsbuild/github.com/dotnet-websharper/build-script/WebSharper.Fake.fsx"

#r "System.Xml.Linq"

#if INTERACTIVE
#r "nuget: NUglify"
#else
#r "paket:
nuget NUglify //"
#endif

open System.IO
open System.Diagnostics
open System.Threading
open Fake.Core
open Fake.Core.TargetOperators
open Fake.DotNet
open Fake.IO
open Fake.IO.FileSystemOperators
open Fake.JavaScript
open WebSharper.Fake

let version = "7.0"
let pre = Some "alpha1"

let baseVersion =
    version + match pre with None -> "" | Some x -> "-" + x
    |> Paket.SemVer.Parse

let publish rids (mode: BuildMode) =
    let publishExe (mode: BuildMode) fw input output explicitlyCopyFsCore =
        for rid in rids do
            let outputPath =
                __SOURCE_DIRECTORY__ </> "build" </> string mode </> output </> fw </> (rid |> Option.defaultValue "") </> "deploy"
            DotNet.publish (fun p ->
                { p with
                    Framework = Some fw
                    OutputPath = Some outputPath
                    NoRestore = true
                    SelfContained = false |> Some
                    Runtime = rid
                    Configuration = mode.AsDotNet
                }) input
            if explicitlyCopyFsCore then
                let fsharpCoreLib = __SOURCE_DIRECTORY__ </> "packages/includes/FSharp.Core/lib/netstandard2.0"
                [ 
                    fsharpCoreLib </> "FSharp.Core.dll" 
                ] 
                |> Shell.copy outputPath                
    publishExe mode "net6.0" "src/compiler/WebSharper.FSharp/WebSharper.FSharp.fsproj" "FSharp" true
    publishExe mode "net6.0" "src/compiler/WebSharper.FSharp.Service/WebSharper.FSharp.Service.fsproj" "FSharp" true
    publishExe mode "net6.0" "src/compiler/WebSharper.CSharp/WebSharper.CSharp.fsproj" "CSharp" true

Target.create "Prepare" <| fun _ ->
    // make netstandardtypes.txt
    let f = FileInfo("src/compiler/WebSharper.Core/netstandardtypes.txt")
    if not f.Exists then
        let asm =
            "packages/includes/NETStandard.Library.Ref/ref/netstandard2.1/netstandard.dll"
            |> Mono.Cecil.AssemblyDefinition.ReadAssembly
        use s = f.OpenWrite()
        use w = new StreamWriter(s)
        w.WriteLine(asm.FullName)
        let rec writeType (t: Mono.Cecil.TypeDefinition) =
            w.WriteLine(t.FullName.Replace('/', '+'))
            Seq.iter writeType t.NestedTypes
        Seq.iter writeType asm.MainModule.Types

    // make msbuild/AssemblyInfo.fs
    let lockFile =
        Paket.LockFile.LoadFrom(__SOURCE_DIRECTORY__ </> "paket.lock")
    let roslynVersion = 
        lockFile
            .GetGroup(Paket.Domain.GroupName "main")
            .GetPackage(Paket.Domain.PackageName "Microsoft.CodeAnalysis.CSharp")
            .Version.AsString
    let fcsVersion = 
        lockFile
            .GetGroup(Paket.Domain.GroupName "fcs")
            .GetPackage(Paket.Domain.PackageName "FSharp.Compiler.Service")
            .Version.AsString
    let inFile = "build/AssemblyInfo.fs" // Generated by WS-GenAssemblyInfo
    let outFile = "msbuild/AssemblyInfo.fs"
    let t =
        String.concat "\r\n" [
            yield File.ReadAllText(inFile)
            yield sprintf "    let [<Literal>] FcsVersion = \"%s\"" fcsVersion
            yield sprintf "    let [<Literal>] RoslynVersion = \"%s\"" roslynVersion
        ]
    if not (File.Exists(outFile) && t = File.ReadAllText(outFile)) then
        File.WriteAllText(outFile, t)

    // make minified scripts
    let needsBuilding input output =
        let i = FileInfo(input)
        let o = FileInfo(output)
        not o.Exists || o.LastWriteTimeUtc < i.LastWriteTimeUtc
    let minify (path: string) =
        let out = Path.ChangeExtension(path, ".min.js")
        if needsBuilding path out then
            let raw = File.ReadAllText(path)
            let mjs = NUglify.Uglify.Js(raw).Code
            File.WriteAllText(Path.ChangeExtension(path, ".min.js"), mjs)
            stdout.WriteLine("Written {0}", out)
    minify "src/compiler/WebSharper.Core.JavaScript/Runtime.js"
    minify "src/stdlib/WebSharper.Main/Json.js"
    minify "src/stdlib/WebSharper.Main/AnimFrame.js"

    // install TypeScript
    Npm.install <| fun o -> 
        { o with 
            WorkingDirectory = "./src/compiler/WebSharper.TypeScriptParser/"
        }

let targets = MakeTargets {
    WSTargets.Default (fun () -> ComputeVersion (Some baseVersion)) with
        HasDefaultBuild = false
        BuildAction =
            BuildAction.Multiple [
                BuildAction.Projects ["WebSharper.Compiler.sln"]
                //BuildAction.Custom (publish [ Some "win-x64" ])
                BuildAction.Custom (publish [ None; Some "win-x64"; Some "linux-x64"; Some "linux-musl-x64"; Some "osx-x64" ])
                BuildAction.Projects ["WebSharper.sln"]
            ]
}

targets.AddPrebuild "Prepare"

Target.create "Build" <| fun o ->
    BuildAction.Multiple [
        BuildAction.Projects ["WebSharper.Compiler.sln"]
    ]
    |> build o (buildModeFromFlag o) 

"WS-Restore"
    ==> "Prepare"
    ==> "Build"

Target.create "Publish" <| fun o ->
    publish [ None ] (buildModeFromFlag o)  
    
Target.create "BuildAll" <| fun o ->
    BuildAction.Multiple [
        BuildAction.Projects ["WebSharper.Compiler.sln"]
        //BuildAction.Custom publish
        BuildAction.Projects ["WebSharper.NoTests.sln"]
    ]
    |> build o (buildModeFromFlag o) 

"Prepare"
    ==> "BuildAll"

Target.create "Tests" <| fun o ->
   BuildAction.Multiple [
       BuildAction.Projects ["WebSharper.Compiler.sln"]
       //BuildAction.Custom publish
       BuildAction.Projects ["WebSharper.sln"]
   ]
   |> build o (buildModeFromFlag o)

"Prepare"
    ==> "Tests"

Target.create "RunCompilerTestsRelease" <| fun _ ->
    if Environment.environVarAsBoolOrDefault "SKIP_CORE_TESTING" false then
        Trace.log "Compiler testing skipped"
    else

    [
        //"tests/WebSharper.Compiler.FSharp.Tests/WebSharper.Compiler.FSharp.Tests.fsproj"
        yield "tests/WebSharper.Core.JavaScript.Tests/WebSharper.Core.JavaScript.Tests.fsproj"
        if System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform System.Runtime.InteropServices.OSPlatform.Windows then 
            yield "tests/WebSharper.CSharp.Analyzer.Tests/WebSharper.CSharp.Analyzer.Tests.fsproj"
    ]
    |> List.iter (
        DotNet.test (fun t ->
            { t with
                NoRestore = true
                Configuration = DotNet.BuildConfiguration.Release
            }
        ) 
    ) 

"WS-BuildRelease"
    ?=> "RunCompilerTestsRelease"
    ?=> "WS-Package"

"RunCompilerTestsRelease"
    ==> "CI-Release"

Target.create "RunSPATestsRelease" <| fun _ ->
    if Environment.environVarAsBoolOrDefault "SKIP_CORE_TESTING" false then
        Trace.log "Chutzpah testing for SPA skipped"
    else
    // TODO resolve cross site issues for automatic testing
    ()

    //let res =
    //    Shell.Exec(
    //        "packages/test/Chutzpah/tools/chutzpah.console.exe", 
    //        "tests/WebSharper.SPA.Tests/index.html /engine Chrome /parallelism 1 /silent /failOnError /showFailureReport"
    //    )
    //if res <> 0 then
    //    failwith "Chutzpah test run failed for SPA tests"

Target.create "RunMainTestsRelease" <| fun _ ->
    if Environment.environVarAsBoolOrDefault "SKIP_CORE_TESTING" false || not <| System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform System.Runtime.InteropServices.OSPlatform.Windows then
        Trace.log "Chutzpah testing skipped"
    else

    Trace.log "Starting Web test project"
    let mutable startedOk = false
    let started = new EventWaitHandle(false, EventResetMode.ManualReset)

    use webTestsProc = new Process()
    webTestsProc.StartInfo.FileName <- @"build\Release\Tests\net6.0\Web.exe"
    webTestsProc.StartInfo.WorkingDirectory <- @"tests\Web"
    webTestsProc.StartInfo.UseShellExecute <- false
    webTestsProc.StartInfo.RedirectStandardOutput <- true
    
    webTestsProc.OutputDataReceived.Add(fun d -> 
        if not (isNull d) then
            if not startedOk then            
                Trace.log d.Data
            if d.Data.Contains("Application started.") then
                startedOk <- true   
                started.Set() |> ignore
    )
    webTestsProc.Exited.Add(fun _ -> 
        if not startedOk then
            failwith "Starting Web test project failed."    
    )

    webTestsProc.Start()
    webTestsProc.BeginOutputReadLine()
    started.WaitOne()
    Thread.Sleep(5000)

    let res =
        Shell.Exec(
            "packages/test/Chutzpah/tools/chutzpah.console.exe", 
            "http://localhost:5000/consoletests /engine Chrome /parallelism 1 /silent /failOnError /showFailureReport"
        )
    webTestsProc.Kill()
    if res <> 0 then
        failwith "Chutzpah test run failed"

"WS-BuildRelease"
    ?=> "RunSPATestsRelease"
    ==> "RunMainTestsRelease"
    ?=> "WS-Package"

"RunMainTestsRelease"
    ==> "CI-Release"

"WS-Restore" ==> "Prepare"
"WS-Stop" ==> "WS-Clean"
"WS-Stop" ==> "WS-Restore"

Target.runOrDefaultWithArguments "Build"
