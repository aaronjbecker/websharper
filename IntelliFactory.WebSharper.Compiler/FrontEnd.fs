// $begin{copyright}
//
// This file is part of WebSharper
//
// Copyright (c) 2008-2013 IntelliFactory
//
// GNU Affero General Public License Usage
// WebSharper is free software: you can redistribute it and/or modify it under
// the terms of the GNU Affero General Public License, version 3, as published
// by the Free Software Foundation.
//
// WebSharper is distributed in the hope that it will be useful, but WITHOUT
// ANY WARRANTY; without even the implied warranty of MERCHANTABILITY or
// FITNESS FOR A PARTICULAR PURPOSE. See the GNU Affero General Public License
// for more details at <http://www.gnu.org/licenses/>.
//
// If you are unsure which license is appropriate for your use, please contact
// IntelliFactory at http://intellifactory.com/contact.
//
// $end{copyright}

module IntelliFactory.WebSharper.Compiler.FrontEnd

open System
open System.Collections.Generic
open System.IO
open System.Reflection
open System.Text
open System.Web.UI
open Mono.Cecil
open IntelliFactory.Core
open IntelliFactory.WebSharper
module M = IntelliFactory.WebSharper.Core.Metadata
module P = IntelliFactory.JavaScript.Packager
module PC = IntelliFactory.WebSharper.PathConventions
module R = IntelliFactory.WebSharper.Compiler.ReflectionLayer
module Re = IntelliFactory.WebSharper.Core.Reflection
module Res = IntelliFactory.WebSharper.Core.Resources
module TSE = IntelliFactory.WebSharper.TypeScriptExporter
module W = IntelliFactory.JavaScript.Writer
type Path = string
type Pref = IntelliFactory.JavaScript.Preferences

[<Literal>]
let EMBEDDED_METADATA = "WebSharper.dep"

[<Literal>]
let EMBEDDED_JS = "WebSharper.js"

[<Literal>]
let EMBEDDED_MINJS = "WebSharper.min.js"

[<Literal>]
let EMBEDDED_DTS = "WebSharper.d.ts"

let readStream (s: Stream) =
    use m = new MemoryStream()
    s.CopyTo(m)
    m.ToArray()

let readResource name (def: AssemblyDefinition) =
    def.MainModule.Resources
    |> Seq.tryPick (function
        | :? EmbeddedResource as r when r.Name = name ->
            use reader = new StreamReader(r.GetResourceStream())
            reader.ReadToEnd() |> Some
        | _ -> None)

let readResourceBytes name (def: AssemblyDefinition) =
    def.MainModule.Resources
    |> Seq.tryPick (function
        | :? EmbeddedResource as r when r.Name = name ->
            use r = r.GetResourceStream()
            Some (readStream r)
        | _ -> None)

type Symbols =
    | Mdb of byte []
    | Pdb of byte []

type AssemblyContent =
    {
        ACContent : string
        ACFileName : string
    }

    member ac.FileName = ac.ACFileName
    member ac.Content = ac.ACContent

    static member Create(name, content) =
        {
            ACContent = content
            ACFileName = name
        }

let (|StringArg|_|) (attr: CustomAttributeArgument) =
    if attr.Type.FullName = "System.String" then
        Some (attr.Value :?> string)
    else
        None

type EmbeddedFile =
    {
        mutable ResContent : string
        ResContentBytes : byte []
        ResContentType : string
        ResName : string
    }

    member ri.GetContentData() =
        Array.copy ri.ResContentBytes

    member ri.Content =
        match ri.ResContent with
        | null ->
            let s = UTF8Encoding(false, true).GetString(ri.ResContentBytes)
            ri.ResContent <- s
            s
        | s -> s

    member ri.ContentType = ri.ResContentType
    member ri.FileName = ri.ResName

    member ri.IsScript =
        match ri.ResContentType with
        | "text/javascript" -> true
        | _ -> false

let parseWebResources (def: AssemblyDefinition) =
    def.CustomAttributes
    |> Seq.choose (fun attr ->
        let wra = "System.Web.UI.WebResourceAttribute"
        if attr.AttributeType.FullName = wra then
            match Seq.toList attr.ConstructorArguments with
            | [StringArg resourceName; StringArg contentType] ->
                readResourceBytes resourceName def
                |> Option.map (fun c ->
                    {
                        ResContent = null
                        ResContentBytes = c
                        ResContentType = contentType
                        ResName = resourceName
                    })
            | _ -> None
        else None)

type Assembly =
    {
        Debug : option<Symbols>
        Definition : AssemblyDefinition
    }

    member this.FullName =
        this.Definition.FullName

    member this.GetScripts() =
        parseWebResources this.Definition
        |> Seq.filter (fun r -> r.IsScript)

    member this.GetContents() =
        parseWebResources this.Definition
        |> Seq.filter (fun r -> not r.IsScript)

    member this.OutputParameters(keyPair) =
        let par = WriterParameters()
        match keyPair with
        | Some kp -> par.StrongNameKeyPair <- kp
        | None -> ()
        par

    member this.RawBytes(kP: option<StrongNameKeyPair>) =
        use s = new System.IO.MemoryStream(16 * 1024)
        this.Definition.Write(s, this.OutputParameters kP)
        s.ToArray()

    member this.Symbols = this.Debug

    member this.Write(kP: option<StrongNameKeyPair>)(path: Path) =
        let par = this.OutputParameters kP
        match this.Debug with
        | Some (Mdb _) ->
            par.WriteSymbols <- true
            par.SymbolWriterProvider <- Mdb.MdbWriterProvider()
        | Some (Pdb _) ->
            par.WriteSymbols <- true
            par.SymbolWriterProvider <- Pdb.PdbWriterProvider()
        | None -> ()
        this.Definition.Write(path, par)

    member this.ReadableJavaScript =
        readResource EMBEDDED_JS this.Definition

    member this.CompressedJavaScript =
        readResource EMBEDDED_MINJS this.Definition

    member this.TypeScriptDeclarations =
        readResource EMBEDDED_DTS this.Definition

[<Sealed>]
type Resolver(aR: AssemblyResolver) =
    let def = DefaultAssemblyResolver()

    let resolve (ref: string) (par: option<ReaderParameters>) =
        let n = AssemblyName(ref)
        match aR.ResolvePath n with
        | Some x ->
            match par with
            | None -> AssemblyDefinition.ReadAssembly(x)
            | Some par -> AssemblyDefinition.ReadAssembly(x, par)
        | None -> def.Resolve(ref)

    interface IAssemblyResolver with

        member x.Resolve(name) =
            resolve name None

        member x.Resolve(name: string, par) =
            resolve name (Some par)

        member x.Resolve(ref: AssemblyNameReference, par: ReaderParameters) =
            let ref = ref.FullName
            resolve ref (Some par)

        member x.Resolve(ref: AssemblyNameReference) =
            let ref = ref.FullName
            resolve ref None

[<Sealed>]
type Loader(aR: AssemblyResolver, log: string -> unit) =

    let load (bytes: byte[]) (symbols: option<Symbols>) (aR: AssemblyResolver) =
        use str = new MemoryStream(bytes)
        let par = ReaderParameters()
        par.AssemblyResolver <- Resolver aR
        par.ReadingMode <- ReadingMode.Deferred
        match symbols with
        | Some (Pdb bytes) ->
            par.ReadSymbols <- true
            par.SymbolReaderProvider <- new Pdb.PdbReaderProvider()
            par.SymbolStream <- new MemoryStream(bytes)
        | Some (Mdb bytes) ->
            par.ReadSymbols <- true
            par.SymbolReaderProvider <- new Mdb.MdbReaderProvider()
            par.SymbolStream <- new MemoryStream(bytes)
        | None -> ()
        let def = AssemblyDefinition.ReadAssembly(str, par)
        {
            Debug = symbols
            Definition = def
        }

    static member Create(res: AssemblyResolver)(log) =
        Loader(res, log)

    member this.LoadRaw(bytes)(symbols) =
        load bytes symbols aR

    member this.LoadFile(path: Path) =
        let bytes = File.ReadAllBytes path
        let p ext = Path.ChangeExtension(path, ext)
        let ex x = File.Exists(p x)
        let rd x = File.ReadAllBytes(p x)
        let symbolsPath =
            if ex ".pdb" then Some (p ".pdb")
            elif ex ".mdb" then Some (p ".mdb")
            else None
        let symbols =
            if ex ".pdb" then Some (Pdb (rd ".pdb"))
            elif ex ".mdb" then Some (Mdb (rd ".mdb"))
            else None
        let aR = aR.SearchPaths [path]
        try
            load bytes symbols aR
        with :? InvalidOperationException ->
            if symbolsPath.IsSome then
                "Failed to load symbols: " + symbolsPath.Value
                |> log
            load bytes None aR

module CecilTools =
    open System.Text

    let writeCompiledMetadata
            (a: AssemblyDefinition)
            (rm: M.AssemblyInfo)
            (meta: Metadata.T)
            (pkg: P.Module)
            (typeScript: string) =
        let pub = ManifestResourceAttributes.Public
        let dep =
            use s = new MemoryStream(8 * 1024)
            Metadata.Serialize s meta
            s.ToArray()
        let prog = P.Package pkg
        let js pref =
            use s = new MemoryStream(8 * 1024)
            let () =
                use w = new StreamWriter(s)
                W.WriteProgram pref w (prog pref)
            s.ToArray()
        let rmdata =
            use s = new MemoryStream(8 * 1024)
            rm.ToStream s
            s.ToArray()
        let rmname = M.AssemblyInfo.EmbeddedResourceName
        EmbeddedResource(rmname, pub, rmdata)
        |> a.MainModule.Resources.Add
        EmbeddedResource(EMBEDDED_METADATA, pub, dep)
        |> a.MainModule.Resources.Add
        if not pkg.IsEmpty then
            EmbeddedResource(EMBEDDED_MINJS, pub, js Pref.Compact)
            |> a.MainModule.Resources.Add
            EmbeddedResource(EMBEDDED_JS, pub, js Pref.Readable)
            |> a.MainModule.Resources.Add
        EmbeddedResource
            (
                EMBEDDED_DTS, pub,
                UTF8Encoding(false, true).GetBytes(typeScript)
            )
        |> a.MainModule.Resources.Add

    let readRuntimeMetadata (a: AssemblyDefinition) =
        let key = M.AssemblyInfo.EmbeddedResourceName
        a.MainModule.Resources
        |> Seq.tryPick (function
            | :? EmbeddedResource as r when r.Name = key ->
                use s = r.GetResourceStream()
                try
                    Some (M.AssemblyInfo.FromStream s)
                with e ->
                    failwithf "Failed to read assembly metadata for: %s" a.FullName
            | _ -> None)

    let readCompiledMetadata (a: AssemblyDefinition) =
        let key = EMBEDDED_METADATA
        a.MainModule.Resources
        |> Seq.tryPick (function
            | :? EmbeddedResource as r when r.Name = key ->
                try
                    use s = r.GetResourceStream()
                    Some (Metadata.Deserialize s)
                with _ ->
                    failwithf "Failed to deserialize metadata for: %s" a.FullName
             | _ ->
                None)

[<Sealed>]
type Content(t: Lazy<string>) =
    static let utf8 = UTF8Encoding(false, true) :> Encoding

    member c.Map(f) =
        Content(lazy f t.Value)

    member c.Write(output: TextWriter) =
        output.Write(t.Value)

    member c.WriteFile(fileName: string, ?encoding: Encoding) =
        let enc = defaultArg encoding utf8
        File.WriteAllText(fileName, t.Value, enc)

    member c.Text = t.Value

type Context =
    {
        Code : IDictionary<Re.AssemblyName, Assembly>
        Infos : list<M.AssemblyInfo>
        Metas : list<Metadata.T>
    }

    member this.LookupAssembly(name: Re.AssemblyName) =
        match this.Code.TryGetValue(name) with
        | true, a -> Some a
        | _ -> None

    member this.LookupAssemblyCode(debug: bool, name: Re.AssemblyName) =
        match this.Code.TryGetValue(name) with
        | true, a -> if debug then a.ReadableJavaScript else a.CompressedJavaScript
        | _ -> None

    static member Get(assemblies: list<Assembly>) =
        let assemblies =
            assemblies
            |> Seq.distinctBy (fun a -> a.Definition.Name.Name)
            |> Seq.toList
        let cM = List.choose (fun a -> CecilTools.readCompiledMetadata a.Definition) assemblies
        let rM = List.choose (fun a -> CecilTools.readRuntimeMetadata a.Definition) assemblies
        let code =
            dict [|
                for a in assemblies do
                    yield (Re.AssemblyName.Parse(a.Definition.FullName), a)
            |]
        { Metas = cM; Infos = rM; Code = code }

let getDependencyNodeForAssembly (a: Assembly) : M.Node =
    let name = Re.AssemblyName.Parse(a.Definition.FullName)
    M.Node.AssemblyNode(name, M.AssemblyMode.CompiledAssembly)

let readWebResource (ty: Type) (name: string) =
    try
        let content =
            let content =
                ty.Assembly.GetManifestResourceNames()
                |> Seq.tryFind (fun x -> x.Contains(name))
                |> Option.bind (fun name ->
                    use s = ty.Assembly.GetManifestResourceStream(name)
                    use r = new StreamReader(s)
                    Some (r.ReadToEnd()))
            defaultArg content ""
        let contentType =
            let cT =
                CustomAttributeData.GetCustomAttributes(ty.Assembly)
                |> Seq.tryPick (fun attr ->
                    if attr.Constructor.DeclaringType = typeof<WebResourceAttribute> then
                        match [for a in attr.ConstructorArguments -> a.Value] with
                        | [(:? string as n); (:? string as contentType)] ->
                            if n.Contains(name)
                                then Some contentType
                                else None
                        | _ -> None
                    else None)
            defaultArg cT "text/plain"
        (content, contentType)
    with e ->
        ("", "text/plain")

let writeStartCode (withScript: bool) (writer: TextWriter) =
    writer.WriteLine()
    if withScript then
        writer.WriteLine("<script type='text/javascript'>")
    writer.WriteLine @"if (typeof IntelliFactory !=='undefined')"
    writer.WriteLine @"  IntelliFactory.Runtime.Start();"
    if withScript then
        writer.WriteLine("</script>")

type ResourceContent =
    {
        Content : string
        ContentType : string
        Name : string
    }

type ResourceContext =
    {
        DebuggingEnabled : bool
        DefaultToHttp : bool
        GetSetting : string -> option<string>
        RenderResource : ResourceContent -> Res.Rendering
    }

type BundleMode =
    | CSS = 0
    | HtmlHeaders = 1
    | JavaScript = 2
    | MinifiedJavaScript = 3
    | TypeScript = 4

module JS = IntelliFactory.JavaScript.Syntax

let docWrite w =
    let str x = JS.Constant (JS.String x)
    JS.Application (JS.Binary (JS.Var "document", JS.BinaryOperator.``.``, str "write"), [str w])
    |> W.ExpressionToString IntelliFactory.JavaScript.Preferences.Compact

[<Sealed>]
type Bundle(resolver: AssemblyResolver, set: list<Assembly>) =
    let logger = Logger.Create ignore 1000
    let loader = Loader.Create resolver ignore

    let context = lazy Context.Get(set)

    let deps =
        lazy
        let context = context.Value
        let mInfo = M.Info.Create context.Infos
        mInfo.GetDependencies [for a in set -> getDependencyNodeForAssembly a]

    let htmlHeadersContext : Res.Context =
        {
            DebuggingEnabled = false
            DefaultToHttp = false
            GetSetting = fun _ -> None
            GetAssemblyRendering = fun _ -> Res.Skip
            GetWebResourceRendering = fun _ _-> Res.Skip
        }

    let renderHtmlHeaders (hw: HtmlTextWriter) (res: Res.IResource) =
        res.Render htmlHeadersContext hw

    let render (mode: BundleMode) (writer: TextWriter) =
        use htmlHeadersWriter =
            match mode with
            | BundleMode.HtmlHeaders -> new HtmlTextWriter(writer)
            | _ -> new HtmlTextWriter(TextWriter.Null)
        let debug =
            match mode with
            | BundleMode.MinifiedJavaScript -> false
            | _ -> true
        let context = context.Value
        let renderAssembly (a: Assembly) =
            match mode with
            | BundleMode.JavaScript -> a.ReadableJavaScript
            | BundleMode.MinifiedJavaScript -> a.CompressedJavaScript
            | BundleMode.TypeScript -> a.TypeScriptDeclarations
            | _ -> None
            |> Option.iter (fun t -> writer.WriteLine(t))
        let renderWebResource (name: string) (cType: string) (c: string) =
            match cType.ToLower(), mode with
            | "text/javascript", BundleMode.JavaScript
            | "text/javascript", BundleMode.MinifiedJavaScript ->
                writer.WriteLine(c)
            | "text/css", BundleMode.CSS ->
                writer.WriteLine(c)
            | _ -> ()
        let ctx : Res.Context =
            {
                DebuggingEnabled = debug
                DefaultToHttp = false // TODO make configurable
                GetAssemblyRendering = fun name ->
                    context.LookupAssembly(name)
                    |> Option.iter renderAssembly
                    Res.Skip
                GetSetting = fun name -> None
                GetWebResourceRendering = fun ty name ->
                    let (c, cT) = readWebResource ty name
                    renderWebResource name cT c
                    Res.Skip
            }
        use htmlWriter = new HtmlTextWriter(TextWriter.Null)
        for d in deps.Value do
            match mode with
            | BundleMode.HtmlHeaders -> renderHtmlHeaders htmlHeadersWriter d
            | _ ->
                d.Render ctx htmlWriter
        match mode with
        | BundleMode.JavaScript | BundleMode.MinifiedJavaScript ->
            writeStartCode false writer
        | _ -> ()

    let content mode =
        let t =
            lazy
            use w = new StringWriter()
            render mode w
            w.ToString()
        Content(t)

    let css = content BundleMode.CSS
    let htmlHeaders = content BundleMode.HtmlHeaders
    let javaScriptHeaders = htmlHeaders.Map(docWrite)
    let javaScript = content BundleMode.JavaScript
    let minifedJavaScript = content BundleMode.MinifiedJavaScript
    let typeScript = content BundleMode.TypeScript

    member b.CSS = css
    member b.HtmlHeaders = htmlHeaders
    member b.JavaScript = javaScript
    member b.JavaScriptHeaders = javaScriptHeaders
    member b.MinifiedJavaScript = minifedJavaScript
    member b.TypeScript = typeScript

    member b.WithAssembly(assemblyFile) =
        let assem = loader.LoadFile(assemblyFile)
        let dir = Path.GetDirectoryName(assemblyFile)
        let resolver = resolver.SearchDirectories([dir])
        Bundle(resolver, assem :: set)

    member b.WithDefaultReferences() =
        let wsHome = Path.GetDirectoryName(typeof<Bundle>.Assembly.Location)
        [|
            "IntelliFactory.WebSharper.Collections"
            "IntelliFactory.WebSharper.Control"
        |]
        |> Seq.map (fun n -> Path.Combine(wsHome, n + ".dll"))
        |> Seq.fold (fun (b: Bundle) x -> b.WithAssembly(x)) b

    member b.WithTransitiveReferences() =
        let comparer =
            HashIdentity.FromFunctions<Assembly>
                (fun a -> hash a.Definition.FullName)
                (fun a b -> a.Definition.FullName = b.Definition.FullName)
        let pred (a: Assembly) =
            a.Definition.MainModule.AssemblyReferences
            |> Seq.choose (fun r ->
                let n = AssemblyName(r.FullName)
                match resolver.ResolvePath(n) with
                | None -> None
                | Some path ->
                    loader.LoadFile(path)
                    |> Some)
        let completeSet =
            Algorithms.TopSort.Do(set, pred, comparer)
            |> Seq.toList
        Bundle(resolver, completeSet)

    static member Empty =
        let resolver = AssemblyResolver.Create()
        Bundle(resolver, [])

    static member Create() =
        Bundle.Empty.WithDefaultReferences()

type Options =
    {
        ErrorLimit : int
        KeyPair : option<StrongNameKeyPair>
        References : list<Assembly>
    }

    static member Default =
        {
            ErrorLimit = 20
            KeyPair = None
            References = []
        }

[<Sealed>]
type CompiledAssembly
    (
        context: Context,
        source: R.AssemblyDefinition,
        meta: Metadata.T,
        aInfo: M.AssemblyInfo,
        mInfo: M.Info,
        pkg: P.Module,
        typeScript: string
    ) =

    let getJS (pref: Pref) =
        use w = new StringWriter()
        W.WriteProgram pref w (P.Package pkg pref)
        w.ToString()

    let compressedJS = lazy getJS Pref.Compact
    let readableJS = lazy getJS Pref.Readable

    let nameOfSelf = Re.AssemblyName.Convert(source.Name)

    let deps =
        lazy
        let self = M.Node.AssemblyNode(nameOfSelf, M.AssemblyMode.CompiledAssembly)
        mInfo.GetDependencies([self])

    member this.AssemblyInfo = aInfo
    member this.CompressedJavaScript = compressedJS.Value
    member this.Info = mInfo
    member this.Metadata = meta
    member this.Package = pkg
    member this.ReadableJavaScript = readableJS.Value
    member this.TypeScriptDeclarations = typeScript

    member this.Dependencies = deps.Value

    member this.RenderDependencies(ctx: ResourceContext, writer: HtmlTextWriter) =
        let pU = PC.PathUtility.VirtualPaths("/")
        let cache = Dictionary()
        let getRendering (content: ResourceContent) =
            match cache.TryGetValue(content) with
            | true, y -> y
            | _ ->
                let y = ctx.RenderResource(content)
                cache.Add(content, y)
                y
        let makeJsUri (name: PC.AssemblyId) js =
            getRendering {
                Content = js
                ContentType = "text/javascript"
                Name =
                    if ctx.DebuggingEnabled then
                        pU.JavaScriptPath(name)
                    else
                        pU.MinifiedJavaScriptPath(name)
            }
        let ctx : Res.Context =
            {
                DebuggingEnabled = ctx.DebuggingEnabled
                DefaultToHttp = ctx.DefaultToHttp
                GetAssemblyRendering = fun name ->
                    if name = nameOfSelf then
                        (if ctx.DebuggingEnabled then Pref.Readable else Pref.Compact)
                        |> getJS
                        |> makeJsUri (PC.AssemblyId.Create name.FullName)
                    else
                        match context.LookupAssemblyCode(ctx.DebuggingEnabled, name) with
                        | Some x -> makeJsUri (PC.AssemblyId.Create name.FullName) x
                        | None -> Res.Skip
                GetSetting = ctx.GetSetting
                GetWebResourceRendering = fun ty name ->
                    let (c, cT) = readWebResource ty name
                    getRendering {
                        Content = c
                        ContentType = cT
                        Name = name
                    }
            }
        this.RenderDependencies(ctx, writer)

    member this.RenderDependencies(ctx, writer: HtmlTextWriter) =
        for d in this.Dependencies do
            d.Render ctx writer
        writeStartCode true writer

[<Sealed>]
type Compiler(errorLimit: int, log: Message -> unit, ctx: Context) =

    member this.Compile(quotation: Quotations.Expr, context: System.Reflection.Assembly, ?name) : option<CompiledAssembly> =
        this.CompileAssembly(R.Dynamic.FromQuotation quotation context (defaultArg name "Example"))

    member this.Compile(quotation: Quotations.Expr, ?name) : option<CompiledAssembly> =
        this.Compile(quotation, System.Reflection.Assembly.GetCallingAssembly(), ?name = name)

    member this.CompileAssembly(assembly: R.AssemblyDefinition) : option<CompiledAssembly> =
        let succ = ref true
        let err (m: Message) =
            match m.Priority with
            | Priority.Warning -> ()
            | _ -> succ := false
            log m
        let logger = Logger.Create err errorLimit
        let meta = Metadata.Union logger ctx.Metas
        let pool = Inlining.Pool.Create logger
        let macros = Reflector.Pool.Create logger
        try
            let ra = Reflector.Reflect logger assembly
            let pkg = Resolver.Resolve logger ra
            let va = Validator.Validate logger pool macros ra
            let rm = Analyzer.Analyze ctx.Infos va
            let local = Metadata.Parse logger va
            let joined = Metadata.Union logger [meta; local]
            Assembler.Assemble logger pool macros joined va
            if !succ then
                let mInfo = M.Info.Create (rm :: ctx.Infos)
                let pkg = pkg.Value
                let tsDecls = TSE.ExportDeclarations joined va
                Some (CompiledAssembly(ctx, assembly, local, rm, mInfo, pkg, tsDecls))
            else None
        with ErrorLimitExceeded -> None

    member this.CompileAndModify(assembly: Assembly) : bool =
        match this.CompileAssembly(R.Cecil.AdaptAssembly assembly.Definition) with
        | None -> false
        | Some a ->
            CecilTools.writeCompiledMetadata assembly.Definition
                a.AssemblyInfo a.Metadata a.Package a.TypeScriptDeclarations
            true

let Prepare (options: Options) (log: Message -> unit) : Compiler =
    let ctx = Context.Get(options.References)
    Compiler(options.ErrorLimit, log, ctx)

let Compile (options: Options) (log: Message -> unit) : Assembly -> bool =
    let c = Prepare options log
    fun aF -> c.CompileAndModify(aF)
