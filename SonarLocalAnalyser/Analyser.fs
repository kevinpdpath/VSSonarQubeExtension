﻿// --------------------------------------------------------------------------------------------------------------------
// <copyright file="Analyser.fs" company="Copyright © 2014 Tekla Corporation. Tekla is a Trimble Company">
//     Copyright (C) 2013 [Jorge Costa, Jorge.Costa@tekla.com]
// </copyright>
// --------------------------------------------------------------------------------------------------------------------
// This program is free software; you can redistribute it and/or modify it under the terms of the GNU Lesser General Public License
// as published by the Free Software Foundation; either version 3 of the License, or (at your option) any later version.
//
// This program is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty
// of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the GNU Lesser General Public License for more details. 
// You should have received a copy of the GNU Lesser General Public License along with this program; if not, write to the Free
// Software Foundation, Inc., 59 Temple Place, Suite 330, Boston, MA 02111-1307 USA
// --------------------------------------------------------------------------------------------------------------------
namespace SonarLocalAnalyser

open System
open System.Runtime.InteropServices
open System.Security.Permissions
open System.Threading
open System.IO
open System.Diagnostics
open System.Collections.Generic
open VSSonarPlugins
open VSSonarPlugins.Types
open VSSonarPlugins.Helpers
open CommonExtensions
open System.Diagnostics
open Microsoft.Build.Utilities
open SonarRestService
open FSharp.Collections.ParallelSeq
open System.Management
open System.Reflection
open System.ComponentModel
open System.Management
open System.Runtime.InteropServices

type LanguageType =
   | SingleLang = 0
   | MultiLang = 1

type AnalyserCommandExec(logger : TaskLoggingHelper, timeout : int64) =
    let addEnvironmentVariable (startInfo:ProcessStartInfo) a b = startInfo.EnvironmentVariables.Add(a, b)

    let output : System.Collections.Generic.List<string> = new System.Collections.Generic.List<string>()
    let error : System.Collections.Generic.List<string> = new System.Collections.Generic.List<string>()

    let KillPrograms(currentProcessName : string) =
        if not(String.IsNullOrEmpty(currentProcessName)) then
            try
                let processId = Process.GetCurrentProcess().Id
                let processes = System.Diagnostics.Process.GetProcessesByName(currentProcessName)

                for proc in processes do
                    if processId <> proc.Id then
                        try
                            Process.GetProcessById(proc.Id).Kill()
                        with
                        | ex -> ()
            with
            | ex -> ()

    let toMap dictionary = 
        (dictionary :> seq<_>)
        |> Seq.map (|KeyValue|)
        |> Map.ofSeq

    member val Logger = logger
    member val stopWatch = Stopwatch.StartNew()
    member val proc : Process  = new Process() with get, set
    member val returncode : ReturnCode = ReturnCode.Ok with get, set
    member val cancelSignal : bool = false with get, set

    member val Program : string = "" with get, set

    member this.killProcess(pid : int32) : bool =
        let mutable didIkillAnybody = false
        try
            let procs = Process.GetProcesses()
            for proc in procs do
                if this.GetParentProcess(proc.Id) = pid then
                    if this.killProcess(proc.Id) = true then
                        didIkillAnybody <- true

            try
                let myProc = Process.GetProcessById(pid)
                myProc.Kill()
                didIkillAnybody <- true
            with
             | ex -> ()
        with
         | ex -> ()

        didIkillAnybody

    member this.TimerControl() =
        async {
            while not this.cancelSignal do
                if this.stopWatch.ElapsedMilliseconds > timeout then

                    if not(obj.ReferenceEquals(logger, null)) then
                        logger.LogError(sprintf "Expired Timer: %x " this.stopWatch.ElapsedMilliseconds)

                    try
                        if this.killProcess(this.proc.Id) then
                            this.returncode <- ReturnCode.Ok
                        else
                            this.returncode <- ReturnCode.NokAppSpecific
                            //this.proc.Kill()
                    with
                     | ex -> ()

                Thread.Sleep(1000)

            if this.stopWatch.ElapsedMilliseconds > timeout then
                this.returncode <- ReturnCode.Timeout
        }

    member this.ProcessErrorDataReceived(e : DataReceivedEventArgs) =
        this.stopWatch.Restart()
        if not(String.IsNullOrWhiteSpace(e.Data)) then
            error.Add(e.Data)
            System.Diagnostics.Debug.WriteLine("ERROR:" + e.Data)
        ()

    member this.ProcessOutputDataReceived(e : DataReceivedEventArgs) =
        this.stopWatch.Restart()
        if not(String.IsNullOrWhiteSpace(e.Data)) then
            output.Add(e.Data)
            System.Diagnostics.Debug.WriteLine(e.Data)
        ()



    member this.GetParentProcess(Id : int32) = 
        let mutable parentPid = 0
        use mo = new ManagementObject("win32_process.handle='" + Id.ToString() + "'")
        let tmp = mo.Get()
        Convert.ToInt32(mo.["ParentProcessId"])

    member this.CancelExecution() =            
        if this.proc.HasExited = false then
            this.proc.Kill()
        this.cancelSignal <- true
        ReturnCode.Ok

    member this.CancelExecutionAndSpanProcess(processNames : string []) =
            
        if this.proc.HasExited = false then
            processNames |> Array.iter (fun name -> KillPrograms(name))
            if this.proc.HasExited = false then
                this.proc.Kill()

        this.cancelSignal <- true
        ReturnCode.Ok

    member this.ResetData() =
        error.Clear()
        output.Clear()
        ()

    member this.ExecuteCommand(program, args, env, outputHandler, errorHandler, workingDir) =        
        this.Program <- program       
        let startInfo = ProcessStartInfo(FileName = program,
                                            Arguments = args,
                                            WindowStyle = ProcessWindowStyle.Normal,
                                            UseShellExecute = false,
                                            RedirectStandardOutput = true,
                                            RedirectStandardError = true,
                                            RedirectStandardInput = true,
                                            CreateNoWindow = true,
                                            WorkingDirectory = workingDir)
        toMap env |> Map.iter (addEnvironmentVariable startInfo)

        this.proc <- new Process(StartInfo = startInfo,
                                    EnableRaisingEvents = true)
        this.proc.OutputDataReceived.Add(fun c -> outputHandler(c))
        this.proc.ErrorDataReceived.Add(errorHandler)
        let ret = this.proc.Start()

        this.stopWatch.Restart()
        Async.Start(this.TimerControl());

        this.proc.BeginOutputReadLine()
        this.proc.BeginErrorReadLine()
        this.proc.WaitForExit()
        this.cancelSignal <- true
        this.proc.ExitCode


[<ComVisible(false)>]
[<HostProtection(SecurityAction.LinkDemand, Synchronization = true, ExternalThreading = true)>]
type SonarLocalAnalyser(plugins : System.Collections.Generic.List<IAnalysisPlugin>, restService : ISonarRestService, vsinter : IConfigurationHelper, notificationManager : INotificationManager) =
    let assemblyRunningPath = Directory.GetParent(Assembly.GetExecutingAssembly().Location).ToString()
    let completionEvent = new DelegateEvent<System.EventHandler>()
    let stdOutEvent = new DelegateEvent<System.EventHandler>()
    let associateCompletedEvent = new DelegateEvent<System.EventHandler>()
    let mutable profileUpdated = false
    let jsonReports : System.Collections.Generic.List<String> = new System.Collections.Generic.List<String>()
    let localissues : System.Collections.Generic.List<Issue> = new System.Collections.Generic.List<Issue>()
    let cachedProfiles : System.Collections.Generic.Dictionary<string, System.Collections.Generic.Dictionary<string, Profile>> =
                    let data = new System.Collections.Generic.Dictionary<string, System.Collections.Generic.Dictionary<string, Profile>>()
                    data
    let syncLock = new System.Object()
    let syncLockProfiles = new System.Object()
    let mutable profilesCnt = 0
    let mutable exec : AnalyserCommandExec = new AnalyserCommandExec(null, int64(1000 * 60 * 60)) // TODO timeout to be passed as parameter
    let mutable sonarConfig : ISonarConfiguration = null

    let initializationDone = 

        vsinter.WriteSetting(new SonarQubeProperties(Key = GlobalAnalysisIds.ExcludedPluginsKey, Value = "devcockpit,pdfreport,report,scmactivity,views,jira,scmstats", Context = Context.AnalysisGeneral, Owner = OwnersId.AnalysisOwnerId), false, true)


        let javaExists = 
            try
                File.Exists(vsinter.ReadSetting(Context.AnalysisGeneral, OwnersId.AnalysisOwnerId, GlobalAnalysisIds.JavaExecutableKey).Value)
            with
            | _ -> false

        if File.Exists("C:\Program Files (x86)\Java\jre8\bin\java.exe") then
            vsinter.WriteSetting(new SonarQubeProperties(Key = GlobalAnalysisIds.JavaExecutableKey, Value = @"C:\Program Files (x86)\Java\jre8\bin\java.exe", Context = Context.AnalysisGeneral, Owner = OwnersId.AnalysisOwnerId), false, javaExists)
        else
            vsinter.WriteSetting(new SonarQubeProperties(Key = GlobalAnalysisIds.JavaExecutableKey, Value = @"C:\Program Files (x86)\Java\jre7\bin\java.exe", Context = Context.AnalysisGeneral, Owner = OwnersId.AnalysisOwnerId), false, javaExists)

        let skipIfExists =
            try
                File.Exists(vsinter.ReadSetting(Context.AnalysisGeneral, OwnersId.AnalysisOwnerId, GlobalAnalysisIds.RunnerExecutableKey).Value)
            with
            | _ -> false

        vsinter.WriteSetting(new SonarQubeProperties(Key = GlobalAnalysisIds.RunnerExecutableKey,
                                                     Value = Path.Combine(assemblyRunningPath, "externalAnalysers", "SonarRunner", "bin", "sonar-runner.bat"),
                                                     Context = Context.AnalysisGeneral,
                                                     Owner = OwnersId.AnalysisOwnerId), false, skipIfExists)

        vsinter.WriteSetting(new SonarQubeProperties(Key = GlobalAnalysisIds.IsDebugAnalysisOnKey, Value = "false", Context = Context.AnalysisGeneral, Owner = OwnersId.AnalysisOwnerId), false, true)
        vsinter.WriteSetting(new SonarQubeProperties(Key = GlobalAnalysisIds.LocalAnalysisTimeoutKey, Value = "10", Context = Context.AnalysisGeneral, Owner = OwnersId.AnalysisOwnerId), false, true)
        true

    let triggerException(x, msg : string, ex : Exception) = 
        let errorInExecution = new LocalAnalysisEventArgs("LA: ", "INVALID OPTIONS: " + msg, ex)
        completionEvent.Trigger([|x; errorInExecution|])

    let GetAPluginInListThatSupportSingleLanguage(project : Resource, conf : ISonarConfiguration) = 
        (List.ofSeq plugins) |> List.find (fun plugin -> plugin.IsProjectSupported(conf, project)) 

    let GetPluginThatSupportsResource(itemInView : VsFileItem) =      
        if itemInView = null then        
            raise(new NoFileInViewException())

        let IsSupported(plugin : IAnalysisPlugin, item : VsFileItem) = 
            let name = plugin.GetPluginDescription().Name
            try
                let prop = vsinter.ReadSetting(Context.GlobalPropsId, name, GlobalIds.PluginEnabledControlId)
                plugin.IsSupported(item) && prop.Value.Equals("true", StringComparison.CurrentCultureIgnoreCase)
            with
            | ex -> plugin.IsSupported(item)
        try
            (List.ofSeq plugins) |> List.find (fun plugin -> IsSupported(plugin, itemInView)) 
        with
        | ex -> null

    let GetExtensionThatSupportsThisProject(conf : ISonarConfiguration, project : Resource) = 
        let plugin = GetAPluginInListThatSupportSingleLanguage(project, conf)
        if plugin <> null then
            plugin.GetLocalAnalysisExtension(conf)
        else
            null

    let GetExtensionThatSupportsThisFile(vsprojitem : VsFileItem, conf:ISonarConfiguration) = 
        let plugin = GetPluginThatSupportsResource(vsprojitem)
        if plugin <> null then
            plugin.GetLocalAnalysisExtension(conf)
        else
            null

    let GetDoubleParent(x, path : string) =
        if String.IsNullOrEmpty(path) then
            triggerException(x, "Path not set in options", new Exception("Path Give in Options was not defined, be sure configuration is correctly set"))
            ""
        else
            let data = Directory.GetParent(Directory.GetParent(path).ToString()).ToString()
            if not(Directory.Exists(data)) then
                triggerException(x, "DirectoryNotFound For Required Variable: " + data, new Exception("Directory Not Foud"))
            data

    let GetParent(x, path : string) =
        if String.IsNullOrEmpty(path) then
            triggerException(x, "Path not set in options", new Exception("Path Give in Options was not defined, be sure configuration is correctly set"))
            ""
        else
            let data = Directory.GetParent(path).ToString()
            if not(Directory.Exists(data)) then
                triggerException(x, "DirectoryNotFound For Required Variable: " + data, new Exception("Directory Not Foud"))
            data

    let GenerateCommonArgumentsForAnalysis(mode : AnalysisMode, version : double, conf : ISonarConfiguration, project : Resource) =
        let builder = new CommandLineBuilder()
        // mandatory properties
        builder.AppendSwitchIfNotNull("-Dsonar.host.url=", conf.Hostname.Trim())
        if not(String.IsNullOrEmpty(conf.Username)) then
            builder.AppendSwitchIfNotNull("-Dsonar.login=", conf.Username)

        if not(String.IsNullOrEmpty(conf.Password)) then
            builder.AppendSwitchIfNotNull("-Dsonar.password=", conf.Password)

        builder.AppendSwitchIfNotNull("-Dsonar.projectKey=", project.Key)
        builder.AppendSwitchIfNotNull("-Dsonar.projectName=", project.Name)
        builder.AppendSwitchIfNotNull("-Dsonar.projectVersion=", project.Version)

        // optional project properties
        try
            let prop = vsinter.ReadSetting(Context.AnalysisProject, project.Key, "sonar.sourceEncoding")
            builder.AppendSwitchIfNotNull("-Dsonar.sourceEncoding=", prop.Value)
        with
        | ex -> builder.AppendSwitchIfNotNull("-Dsonar.sourceEncoding=", "UTF-8")


        let excludedPlugins = vsinter.ReadSetting(Context.AnalysisGeneral, OwnersId.AnalysisOwnerId, GlobalAnalysisIds.ExcludedPluginsKey).Value

        // analysis
        if version >= 4.0 && not(mode.Equals(AnalysisMode.Full)) then
            match mode with
            | AnalysisMode.Incremental -> builder.AppendSwitchIfNotNull("-Dsonar.analysis.mode=", "incremental")
                                          builder.AppendSwitchIfNotNull("-Dsonar.preview.excludePlugins=", excludedPlugins)

            | AnalysisMode.Preview -> builder.AppendSwitchIfNotNull("-Dsonar.analysis.mode=", "preview")
                                      builder.AppendSwitchIfNotNull("-Dsonar.preview.excludePlugins=", excludedPlugins)
            | _ -> ()

        elif version >= 3.4 && not(mode.Equals(AnalysisMode.Full)) then
            match mode with
            | AnalysisMode.Incremental -> raise (new InvalidOperationException("Analysis Method Not Available in this Version of SonarQube"))
            | AnalysisMode.Preview -> builder.AppendSwitchIfNotNull("-Dsonar.dryRun=", "true")
                                      builder.AppendSwitchIfNotNull("-Dsonar.dryRun.excludePlugins=", excludedPlugins)
            | _ -> ()
        elif not(mode.Equals(AnalysisMode.Full)) then
            raise (new InvalidOperationException("Analysis Method Not Available in this Version of SonarQube"))
                   
        builder.AppendSwitch("-X")                    

        builder

    let SetupEnvironment(x, platform : string, configuration : string)=
        let javaexec = vsinter.ReadSetting(Context.AnalysisGeneral, OwnersId.AnalysisOwnerId, GlobalAnalysisIds.JavaExecutableKey).Value
        let runnerexec = vsinter.ReadSetting(Context.AnalysisGeneral, OwnersId.AnalysisOwnerId, GlobalAnalysisIds.RunnerExecutableKey).Value

        let javahome = GetDoubleParent(x, javaexec)
        let sonarrunnerhome = GetDoubleParent(x, runnerexec)
        let path = GetParent(x, javaexec) + ";" + GetParent(x, runnerexec) + ";" + Environment.GetEnvironmentVariable("PATH")
                   
        if not(Environment.GetEnvironmentVariable("JAVA_HOME") = null) then
            Environment.SetEnvironmentVariable("JAVA_HOME", "")

        if not(Environment.GetEnvironmentVariable("SONAR_RUNNER_HOME") = null) then
            Environment.SetEnvironmentVariable("SONAR_RUNNER_HOME", "")

        if not(Environment.GetEnvironmentVariable("PATH") = null) then
            Environment.SetEnvironmentVariable("PATH", "")

        if not(Environment.GetEnvironmentVariable("Platform") = null) then
            Environment.SetEnvironmentVariable("Platform", "")

        if not(Environment.GetEnvironmentVariable("Configuration") = null) then
            Environment.SetEnvironmentVariable("Configuration", "")

        let cleanPlat = platform.Replace(" ", "")
        let cleanConf = configuration.Replace(" ", "")
        Map.ofList [("PATH", path); ("JAVA_HOME", javahome); ("SONAR_RUNNER_HOME", sonarrunnerhome); ("Platform", cleanPlat); ("Configuration", cleanConf)]


    let monitor = new Object()
    let numberofFilesToAnalyse = ref 0;
      
    member val Abort = false with get, set
    member val ProjectRoot = "" with get, set
    member val Project = null : Resource with get, set
    member val Version = 0.0 with get, set
    member val Conf = null with get, set
    member val ExecutingThread = null with get, set
    member val CurrentAnalysisType = AnalysisMode.File with get, set
    member val RunningLanguageMethod = LanguageType.SingleLang with get, set
    member val ItemInView = null : VsFileItem with get, set
    member val OnModifyFiles = false with get, set
    member val AnalysisIsRunning = false with get, set
    member val ProjectLookupRunning = Set.empty with get, set

    member val SqTranslator : ISQKeyTranslator = null with get, set
    member val VsInter : IVsEnvironmentHelper = null with get, set


    member x.ProcessExecutionEventComplete(e : EventArgs) =
        let data = e :?> LocalAnalysisEventArgs
        let message = new LocalAnalysisEventArgs("LA: ", data.ErrorMessage, null)
        completionEvent.Trigger([|x; message|])
          
    member x.ProcessOutputPluginData(e : EventArgs) = 
        let data = e :?> LocalAnalysisEventArgs
        let message = new LocalAnalysisEventArgs("LA:", data.ErrorMessage, null)
        stdOutEvent.Trigger([|x; message|])

    member x.ProcessOutputDataReceived(e : DataReceivedEventArgs) =
        try
            if e.Data <> null && not(x.Abort) then
                if e.Data.EndsWith(".json") then
                    let elems = e.Data.Split(' ');
                    jsonReports.Add(elems.[elems.Length-1])

                let isDebugOn = vsinter.ReadSetting(Context.AnalysisGeneral, OwnersId.AnalysisOwnerId, GlobalAnalysisIds.IsDebugAnalysisOnKey).Value

                if e.Data.Contains(" DEBUG - ") then
                    if isDebugOn.Equals("True") then
                        let message = new LocalAnalysisEventArgs("LA:", e.Data, null)
                        stdOutEvent.Trigger([|x; message|])
                else
                    let message = new LocalAnalysisEventArgs("LA:", e.Data, null)
                    stdOutEvent.Trigger([|x; message|])

                if e.Data.Contains("DEBUG - Populating index from") then
                    let message = new LocalAnalysisEventArgs("LA:", e.Data, null)
                    stdOutEvent.Trigger([|x; message|])
                    let separator = [| "Populating index from" |]
                    let mutable fileName = e.Data.Split(separator, StringSplitOptions.RemoveEmptyEntries).[1]

                    if fileName.Contains("abs=") then 
                        let separator = [| "abs=" |]
                        let absfileName = fileName.Split(separator, StringSplitOptions.RemoveEmptyEntries).[1].Trim()
                        fileName <- absfileName.TrimEnd(']')

                    if File.Exists(fileName) then
                        let vsprojitem = new VsFileItem(FileName = Path.GetFileName(fileName), FilePath = fileName)
                        let plugin = GetPluginThatSupportsResource(vsprojitem)
                        let extension = 
                            if plugin <> null then
                                plugin.GetLocalAnalysisExtension(x.Conf)
                            else
                                null

                        let extension = GetExtensionThatSupportsThisFile(vsprojitem, x.Conf)
                        if extension <> null then
                            let plugin = GetPluginThatSupportsResource(vsprojitem)
                            let project = new Resource()
                            project.Date <- x.Project.Date
                            project.Lang <- x.Project.Lang
                            project.Id <- x.Project.Id
                            project.Key <- x.Project.Key
                            project.Lname <- x.Project.Lname
                            project.Version <- x.Project.Version
                            project.Name <- x.Project.Name
                            project.Qualifier <- x.Project.Qualifier
                            project.Scope <- x.Project.Scope
                            project.Lang <- plugin.GetLanguageKey(vsprojitem)

                            let profile = cachedProfiles.[project.Name].[plugin.GetLanguageKey(vsprojitem)]

                            let issues = extension.ExecuteAnalysisOnFile(vsprojitem, profile, project, x.Conf)
                            lock syncLock (
                                fun () -> 
                                    localissues.AddRange(issues)
                                )
            with
            | ex -> ()


    member x.generateCommandArgs(mode : AnalysisMode, version : double, conf : ISonarConfiguration, project : Resource) =
        GenerateCommonArgumentsForAnalysis(mode, version, conf, project).ToString()        
                            
    member x.RunPreviewBuild() =
        try
            jsonReports.Clear()
            let runnerexec = vsinter.ReadSetting(Context.AnalysisGeneral, OwnersId.AnalysisOwnerId, GlobalAnalysisIds.RunnerExecutableKey).Value


            let incrementalArgs = x.generateCommandArgs(AnalysisMode.Preview, x.Version, x.Conf, x.Project)
            if not(String.IsNullOrEmpty(x.Conf.Password)) then
                let message = sprintf "[%s] %s %s" x.ProjectRoot runnerexec (incrementalArgs.Replace(x.Conf.Password, "xxxxx"))
                let commandData = new LocalAnalysisEventArgs("CMD: ", message, null)
                stdOutEvent.Trigger([|x; commandData|])               
        
            let errorCode = exec.ExecuteCommand(runnerexec, incrementalArgs, SetupEnvironment(x,  x.Project.ActivePlatform, x.Project.ActiveConfiguration), x.ProcessOutputDataReceived, x.ProcessOutputDataReceived, x.ProjectRoot)
            if errorCode > 0 then
                let errorInExecution = new LocalAnalysisEventArgs("LA", "Failed To Execute", new Exception("Error Code: " + errorCode.ToString()))
                completionEvent.Trigger([|x; errorInExecution|])               
            else
                completionEvent.Trigger([|x; null|])
        with
        | ex ->
            let errorInExecution = new LocalAnalysisEventArgs("LA", "Exception while executing", ex)
            completionEvent.Trigger([|x; errorInExecution|])

        x.AnalysisIsRunning <- false

    member x.RunFullBuild() =
        try
            jsonReports.Clear()
            localissues.Clear()
            let runnerexec = vsinter.ReadSetting(Context.AnalysisGeneral, OwnersId.AnalysisOwnerId, GlobalAnalysisIds.RunnerExecutableKey).Value

            let incrementalArgs = x.generateCommandArgs(AnalysisMode.Full, x.Version, x.Conf, x.Project)

            if not(String.IsNullOrEmpty(x.Conf.Password)) then
                let message = sprintf "[%s] %s %s" x.ProjectRoot runnerexec (incrementalArgs.Replace(x.Conf.Password, "xxxxx"))
                let commandData = new LocalAnalysisEventArgs("TODO SORT KEY", message, null)
                stdOutEvent.Trigger([|x; commandData|])
          
            let errorCode = exec.ExecuteCommand(runnerexec, incrementalArgs, SetupEnvironment(x,  x.Project.ActivePlatform, x.Project.ActiveConfiguration), x.ProcessOutputDataReceived, x.ProcessOutputDataReceived, x.ProjectRoot)
            if errorCode > 0 then
                let errorInExecution = new LocalAnalysisEventArgs("LA", "Failed To Execute", new Exception("Error Code: " + errorCode.ToString()))
                completionEvent.Trigger([|x; errorInExecution|])               
            else
                completionEvent.Trigger([|x; null|])
        with
        | ex ->
            let errorInExecution = new LocalAnalysisEventArgs("LA", "Exception while executing", ex)
            completionEvent.Trigger([|x; errorInExecution|])

        x.AnalysisIsRunning <- false

    member x.RunIncrementalBuild() = 
        try
            jsonReports.Clear()
            localissues.Clear()
            let runnerexec = vsinter.ReadSetting(Context.AnalysisGeneral, OwnersId.AnalysisOwnerId, GlobalAnalysisIds.RunnerExecutableKey).Value

            let incrementalArgs = x.generateCommandArgs(AnalysisMode.Incremental, x.Version, x.Conf, x.Project)

            if not(String.IsNullOrEmpty(x.Conf.Password)) then
                let message = sprintf "[%s] %s %s" x.ProjectRoot runnerexec (incrementalArgs.Replace(x.Conf.Password, "xxxxx"))
                let commandData = new LocalAnalysisEventArgs("CMD: ", message, null)
                stdOutEvent.Trigger([|x; commandData|])         
         
            let errorCode = exec.ExecuteCommand(runnerexec, incrementalArgs, SetupEnvironment(x,  x.Project.ActivePlatform, x.Project.ActiveConfiguration), x.ProcessOutputDataReceived, x.ProcessOutputDataReceived, x.ProjectRoot)
            if errorCode > 0 then
                let errorInExecution = new LocalAnalysisEventArgs("LA", "Failed To Execute", new Exception("Error Code: " + errorCode.ToString()))
                completionEvent.Trigger([|x; errorInExecution|])               
            else
                completionEvent.Trigger([|x; null|])
        with
        | ex ->
            let errorInExecution = new LocalAnalysisEventArgs("LA", "Exception while executing", ex)
            completionEvent.Trigger([|x; errorInExecution|])

        x.AnalysisIsRunning <- false

    member x.RunFileAnalysisThread() = 

            let plugin = GetPluginThatSupportsResource(x.ItemInView)
            if plugin = null then
                let errorInExecution = new LocalAnalysisEventArgs("Local Analyser", "Cannot Run Analyis: Not Supported: ", new ResourceNotSupportedException())
                completionEvent.Trigger([|x; errorInExecution|])
                x.AnalysisIsRunning <- false
            else
                let GetSourceFromServer() = 
                    try
                        let keyOfItem = (x :> ISonarLocalAnalyser).GetResourceKey(x.ItemInView, true)
                        let source = restService.GetSourceForFileResource(x.Conf, keyOfItem)
                        VsSonarUtils.GetLinesFromSource(source, "\r\n")
                    with
                    | ex ->
                        try
                            let keyOfItem = (x :> ISonarLocalAnalyser).GetResourceKey(x.ItemInView, false)
                            let source = restService.GetSourceForFileResource(x.Conf, keyOfItem)
                            VsSonarUtils.GetLinesFromSource(source, "\r\n")
                        with
                        | ex -> ""
               
                let extension = plugin.GetLocalAnalysisExtension(x.Conf)
                if extension <> null then
                    try
                        if cachedProfiles.ContainsKey(x.Project.Name) then
                            if cachedProfiles.[x.Project.Name].ContainsKey(plugin.GetLanguageKey(x.ItemInView)) then
                                let profile = cachedProfiles.[x.Project.Name].[plugin.GetLanguageKey(x.ItemInView)]
                                let issues = extension.ExecuteAnalysisOnFile(x.ItemInView, profile, x.Project, x.Conf)

                                for issue in issues do
                                    issue.Component <- x.SqTranslator.TranslatePath(x.ItemInView, x.VsInter, restService, sonarConfig)

                                lock syncLock (
                                    fun () ->
                                        localissues.AddRange(issues)
                                    )
                        else
                            (x :> ISonarLocalAnalyser).AssociateWithProject(x.Project, x.Conf)

                    with
                    | ex -> 
                        notificationManager.ReportMessage(new Message(Id = "Analyser", Data = "Failed to analyse file : " + x.ItemInView.FilePath + " : " + ex.Message))
                        notificationManager.ReportException(ex)

                completionEvent.Trigger([|x; null|])
                x.AnalysisIsRunning <- false

    member x.CancelExecution(thread : Thread) =
        if not(obj.ReferenceEquals(exec, null)) then
            exec.CancelExecution |> ignore
        ()
                   
    member x.IsMultiLanguageAnalysis(res : Resource) =

        if plugins = null then
            raise(new NoPluginInstalledException())
        elif res = null then
            raise(new ProjectNotAssociatedException())
        elif String.IsNullOrEmpty(res.Lang) then
            true
        else
            false

    interface ISonarLocalAnalyser with
        [<CLIEvent>]
        member x.LocalAnalysisCompleted = completionEvent.Publish

        [<CLIEvent>]
        member x.StdOutEvent = stdOutEvent.Publish

        [<CLIEvent>]
        member x.AssociateCommandCompeted = associateCompletedEvent.Publish

        member x.RunProjectAnalysis(project : VsProjectItem, conf : ISonarConfiguration) =
            if profileUpdated then
                let plugin = GetAPluginInListThatSupportSingleLanguage(project.Solution.SonarProject, conf)
                if plugin <> null then
                    plugin.LaunchAnalysisOnProject(project, conf)

        member x.GetIssues(conf : ISonarConfiguration, project : Resource) =
            let issues = new System.Collections.Generic.List<Issue>()

            let ParseReport(report : string) =
                if File.Exists(report) then
                    try
                        issues.AddRange(restService.ParseReportOfIssues(report))
                    with
                    | ex -> try
                                issues.AddRange(restService.ParseDryRunReportOfIssues(report));
                            with 
                            | ex -> try
                                        issues.AddRange(restService.ParseReportOfIssuesOld(report));
                                    with
                                    | ex -> ()

            List.ofSeq jsonReports |> Seq.iter (fun m -> ParseReport(m))
            let _lock = new Object()

            let GetIssuesFromPlugins(plug : IAnalysisPlugin) =
                let extension = plug.GetLocalAnalysisExtension(conf)
                try
                    let AddIssueToLocalIssues(c : Issue) =
                        if extension.IsIssueSupported(c, conf) then
                            try
                                let profile = cachedProfiles.[project.Name].[plug.GetLanguageKey(new VsFileItem(FileName = c.Component))]
                                let rule = profile.GetRule(c.Rule)
                                c.Debt <- rule.DebtRemFnCoeff
                            with
                            | ex -> ()
                            lock _lock (fun () -> localissues.Add(c))

                    issues |> PSeq.iter (fun c -> AddIssueToLocalIssues(c))
                with
                | ex -> ()

            List.ofSeq plugins |> Seq.iter (fun m -> GetIssuesFromPlugins(m))

            localissues

        member x.AnalyseFile(itemInView : VsFileItem,
                             project : Resource,
                             onModifiedLinesOnly : bool,
                             version : double,
                             conf : ISonarConfiguration,
                             sqTranslator : ISQKeyTranslator,
                             vsInter : IVsEnvironmentHelper) =
            if profileUpdated then
                x.CurrentAnalysisType <- AnalysisMode.File
                x.Conf <- conf
                x.Version <- version
                x.Abort <- false
                x.Project <- project
                x.ItemInView <- itemInView
                x.OnModifyFiles <- onModifiedLinesOnly
                x.SqTranslator <- sqTranslator
                x.VsInter <- vsInter

                if plugins = null then
                    raise(new ResourceNotSupportedException())

                if itemInView = null then
                    raise(new NoFileInViewException())

                if GetPluginThatSupportsResource(x.ItemInView) = null then
                    raise(new ResourceNotSupportedException())
            
                x.AnalysisIsRunning <- true
                localissues.Clear()
                (new Thread(new ThreadStart(x.RunFileAnalysisThread))).Start()

        member x.RunIncrementalAnalysis(project : Resource, version : double, conf : ISonarConfiguration) =
            if profileUpdated then
                x.CurrentAnalysisType <- AnalysisMode.Incremental
                x.ProjectRoot <- project.SolutionRoot
                x.Conf <- conf
                x.Version <- version
                x.Abort <- false
                x.Project <- project

                if not(String.IsNullOrEmpty(project.Lang)) then
                    x.RunningLanguageMethod <- LanguageType.SingleLang
                else
                    x.RunningLanguageMethod <- LanguageType.MultiLang

                localissues.Clear()
                x.AnalysisIsRunning <- true
                (new Thread(new ThreadStart(x.RunIncrementalBuild))).Start()
            
        member x.RunPreviewAnalysis(project : Resource, version : double, conf : ISonarConfiguration) =
            if profileUpdated then
                x.CurrentAnalysisType <- AnalysisMode.Preview
                x.ProjectRoot <- project.SolutionRoot
                x.Project <- project
                x.Conf <- conf
                x.Version <- version
                x.Abort <- false

                if not(String.IsNullOrEmpty(project.Lang)) then
                    x.RunningLanguageMethod <- LanguageType.SingleLang
                else
                    x.RunningLanguageMethod <- LanguageType.MultiLang

                localissues.Clear()
                x.AnalysisIsRunning <- true
                (new Thread(new ThreadStart(x.RunPreviewBuild))).Start()
                                
        member x.RunFullAnalysis(project : Resource,  version : double, conf : ISonarConfiguration) =
            if profileUpdated then
                x.CurrentAnalysisType <- AnalysisMode.Full
                x.ProjectRoot <- project.SolutionRoot
                x.Project <- project
                x.Conf <- conf
                x.Version <- version
                x.Abort <- false

                if project.Lang = null then
                    x.RunningLanguageMethod <- LanguageType.MultiLang

                localissues.Clear()
                x.AnalysisIsRunning <- true
                (new Thread(new ThreadStart(x.RunFullBuild))).Start()

        member x.StopAllExecution() =
            x.Abort <- true
            x.AnalysisIsRunning <- false
            if x.ExecutingThread = null then
                ()
            else
                if not(obj.ReferenceEquals(exec, null)) then
                    try
                        exec.CancelExecution |> ignore
                    with
                    | ex -> ()

        member x.IsExecuting() =
            x.AnalysisIsRunning
                
        member x.GetResourceKey(itemInView : VsFileItem, safeIsOn:bool) = 
            if plugins = null then
                raise(new ResourceNotSupportedException())

            if itemInView = null then
                raise(new NoFileInViewException())

            let plugin = GetPluginThatSupportsResource(itemInView)

            if plugin = null then
                raise(new ResourceNotSupportedException())

            plugin.GetResourceKey(itemInView, safeIsOn)

        member x.AssociateWithProject(project : Resource, conf:ISonarConfiguration) =
            let GetQualityProfiles(conf:ISonarConfiguration, project:Resource) =
                if cachedProfiles.ContainsKey(project.Name) then
                    profileUpdated <- true
                    associateCompletedEvent.Trigger([|x; null|])
                else
                    let profiles = restService.GetQualityProfilesForProject(conf, project.Key)
                    profilesCnt <- profiles.Count - 1
                    for profile in profiles do
                        let worker2 = new BackgroundWorker()

                        worker2.DoWork.Add(fun c -> 
                            notificationManager.ReportMessage(new Message(Id = "Analyser", Data = "updating profile: " + profile.Name + " : " + profile.Language))

                            lock syncLockProfiles (
                                fun () -> 
                                    try
                                        System.Diagnostics.Debug.WriteLine("Get Profile: " + profile.Name + " : " + profile.Language)
                                        restService.GetRulesForProfile(conf, profile, false)
                                        if cachedProfiles.ContainsKey(project.Name) then
                                            cachedProfiles.[project.Name].Add(profile.Language, profile)
                                        else
                                            let entry = new System.Collections.Generic.Dictionary<string, Profile>()
                                            cachedProfiles.Add(project.Name, entry)
                                            cachedProfiles.[project.Name].Add(profile.Language, profile)
                                    with
                                    | ex -> System.Diagnostics.Debug.WriteLine("cannot add profile: " + ex.Message + " : " + ex.StackTrace)
                                )
                            )

                        worker2.RunWorkerCompleted.Add(fun c ->
                            notificationManager.ReportMessage(new Message(Id = "Analyser", Data = "Completed update profile: " + profile.Name + " : " + profile.Language + " : " + (profilesCnt.ToString()) + " remaining"))
                            profilesCnt <- profilesCnt - 1
                            if profilesCnt = 0 then
                                profileUpdated <- true
                                associateCompletedEvent.Trigger([|x; null|])
                        )

                        worker2.RunWorkerAsync()

            if project <> null && conf <> null then
                sonarConfig <- conf
                let processLine(line : string) =
                    if line.Contains("=") then
                        let vals = line.Split('=')
                        let key = vals.[0].Trim()
                        let value = vals.[1].Trim()
                        let prop = new SonarQubeProperties(Key = key, Value = value, Context = Context.AnalysisProject, Owner = project.Key)
                        vsinter.WriteSetting(prop, true)
                    ()

                try 
                    let projectFile = vsinter.ReadSetting(Context.AnalysisGeneral, project.Key, GlobalAnalysisIds.PropertiesFileKey).Value
                    if File.Exists(projectFile) then
                        let lines = File.ReadAllLines(projectFile)
                        lines |> Seq.iter (fun line -> processLine(line))
                with
                | ex -> let projectFile = Path.Combine(project.SolutionRoot, "sonar-project.properties")
                        if File.Exists(projectFile) then
                            vsinter.WriteSetting(new SonarQubeProperties(Key = GlobalAnalysisIds.PropertiesFileKey, Value = "sonar-project.properties", Context = Context.AnalysisGeneral, Owner = OwnersId.AnalysisOwnerId))
                            let lines = File.ReadAllLines(projectFile)
                            lines |> Seq.iter (fun line -> processLine(line))

                profileUpdated <- false
                notificationManager.ReportMessage(new Message(Id = "Analyser", Data = "Start Update profile"))
                GetQualityProfiles(conf, project)


