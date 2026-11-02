using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

var root = Directory.GetCurrentDirectory();
var evidence = Path.Combine(root, "tests/reorganization-evidence");
var originals = JsonDocument.Parse(File.ReadAllText(Path.Combine(evidence, "original-sources.json")))
    .RootElement.GetProperty("files").EnumerateArray()
    .Where(f => f.GetProperty("path").GetString()!.StartsWith("PackCall.Core.Test/") && f.GetProperty("path").GetString()!.EndsWith(".cs"))
    .Select(f => new Source(f.GetProperty("path").GetString()!, f.GetProperty("source").GetString()!)).ToList();
var finals = Directory.EnumerateFiles(Path.Combine(root, "PackCall.Test"), "*.cs", SearchOption.AllDirectories)
    .Where(p => !Path.GetRelativePath(root,p).Split(Path.DirectorySeparatorChar).Any(s => s is "obj" or "bin"))
    .Select(p => new Source(Path.GetRelativePath(root,p).Replace('\\','/'), File.ReadAllText(p))).ToList();
var options = new JsonSerializerOptions { WriteIndented = true };
var oldMembers = originals.SelectMany(Members).ToList();
var newMembers = finals.SelectMany(Members).ToList();
var remaining = newMembers.ToList();
var memberMapping = new List<object>();
foreach (var old in oldMembers)
{
    var candidates = remaining.Where(n => Canonical(n) == Canonical(old) && AllowedLocation(old,n)).ToList();
    if (candidates.Count == 0) throw new Exception($"Missing or changed member: {old.Source}:{old.Node.Kind()} {old.Node}");
    var updated = candidates[0];
    remaining.Remove(updated);
    memberMapping.Add(new { old = old.Source, @new = updated.Source, kind = old.Node.Kind().ToString(), name = MemberName(old.Node),
        normalized_source_sha256 = Hash(Canonical(old)), source_preserved = true,
        modifier_change = old.Node.Modifiers().ToFullString().Trim() == updated.Node.Modifiers().ToFullString().Trim() ? null : "private -> private protected in extracted support" });
}
if (remaining.Count != 0) throw new Exception("Unexpected added members: " + string.Join("; ", remaining.Select(n => n.Source+":"+MemberName(n.Node))));

var oldTests = oldMembers.Where(m => IsTest(m.Node)).ToList();
var newTests = newMembers.Where(m => IsTest(m.Node)).ToList();
var tests = new List<object>();
var rename = new Dictionary<string,string>();
foreach (var old in oldTests)
{
    var updated = newTests.Single(n => Canonical(n) == Canonical(old));
    var method = (MethodDeclarationSyntax)old.Node;
    var oldIdentity = Identity(old.Node); var newIdentity = Identity(updated.Node);
    rename.Add(oldIdentity,newIdentity);
    tests.Add(new { old = old.Source, @new = updated.Source, old_identity = oldIdentity, new_identity = newIdentity,
        method = method.Identifier.ValueText, inline_cases = method.AttributeLists.SelectMany(a => a.Attributes).Where(a => a.Name.ToString() == "InlineData").Select(a => string.Join(",", a.ArgumentList!.Arguments.Select(v=>v.ToString()))).ToArray(),
        expression_bodied = method.ExpressionBody is not null, method_and_attributes_sha256 = Hash(Canonical(old)), identical = true });
}
if (oldTests.Count != newTests.Count) throw new Exception("Test method count mismatch");

// Class containers may split, gain selection traits or inherit the common lifecycle.
// Every other container name, constraint, member, comment and using directive is checked below.
var oldTypes = originals.SelectMany(s => s.Tree.DescendantNodes().OfType<TypeDeclarationSyntax>().Select(n => (s.Path,Node:n))).ToList();
var newTypes = finals.SelectMany(s => s.Tree.DescendantNodes().OfType<TypeDeclarationSyntax>().Select(n => (s.Path,Node:n))).ToList();
foreach (var (path,type) in oldTypes)
{
    var name = type.Identifier.ValueText;
    var expectedName = name == "CampaignPausePostgresTests" && Path.GetFileNameWithoutExtension(path) != name ? Path.GetFileNameWithoutExtension(path) : name;
    var updated = newTypes.Single(t => t.Node.Identifier.ValueText == expectedName && (Path.GetFileName(t.Path) == Path.GetFileName(path) || expectedName == "OperatorServer" && Path.GetFileName(t.Path) == "OperatorServer.cs" || expectedName == "SequenceRandom" && t.Path == "PackCall.Test/Support/Operators/OperatorTestBase.cs")).Node;
    var expected = type.WithIdentifier(SyntaxFactory.Identifier(expectedName));
    expected = expected.WithMembers(default).WithModifiers(SyntaxFactory.TokenList(expected.Modifiers.Where(t => !t.IsKind(SyntaxKind.PartialKeyword))));
    var actual = updated.WithMembers(default).WithAttributeLists(SyntaxFactory.List(updated.AttributeLists.Where(a => !a.Attributes.Any(v => v.Name.ToString() == "Trait"))));
    if(name == "CampaignPausePostgresTests")
    {
        expected = expected.WithBaseList(null);
        actual = actual.WithBaseList(null);
        if(!type.AttributeLists.Any(a=>a.Attributes.Any(v=>v.Name.ToString()=="Collection")))
            actual=actual.WithAttributeLists(SyntaxFactory.List(actual.AttributeLists.Where(a=>!a.Attributes.Any(v=>v.Name.ToString()=="Collection"))));
        if(updated.BaseList?.Types.Single().Type.ToString() != "PostgresTestBase") throw new Exception("Missing Postgres base");
        if(!updated.AttributeLists.SelectMany(a=>a.Attributes).Any(a=>a.Name.ToString()=="Collection" && a.ArgumentList?.Arguments.Single().ToString()=="\"Measurement\"")) throw new Exception("Missing Measurement collection");
    }
    if(name=="TestOperatorTests")
    {
        if(updated.BaseList?.Types.Single().Type.ToString()!="OperatorTestBase") throw new Exception("Missing operator support base");
        actual=actual.WithBaseList(null);
    }
    if(name=="SequenceRandom") actual=actual.WithModifiers(SyntaxFactory.TokenList(actual.Modifiers.Where(t=>!t.IsKind(SyntaxKind.ProtectedKeyword))));
    if(Tokens(expected) != Tokens(actual)) throw new Exception("Unexpected container change: "+path+":"+name);
}
var baseType = newTypes.Single(t=>t.Node.Identifier.ValueText=="PostgresTestBase").Node;
if(baseType.BaseList?.Types.Single().Type.ToString() != "IAsyncLifetime" || !baseType.Modifiers.Any(t=>t.IsKind(SyntaxKind.AbstractKeyword)) || !baseType.Modifiers.Any(t=>t.IsKind(SyntaxKind.PublicKeyword))) throw new Exception("Postgres lifecycle base contract changed");
var addedTypes = newTypes.Where(t => !oldTypes.Any(o => o.Node.Identifier.ValueText == t.Node.Identifier.ValueText) && !new[]{"WorkerPostgresTests","DeliveryResultLeasePostgresTests","OperatorPostgresTests","PerformancePostgresTests","OperatorRegistrationTests","OperatorTestBase"}.Contains(t.Node.Identifier.ValueText)).ToList();
if(addedTypes.Count != 1 || addedTypes[0].Node.Identifier.ValueText != "PostgresTestBase") throw new Exception("Unexpected added type");

var traitMapping=new List<object>();
foreach(var (path,type) in newTypes.Where(t=>t.Node.Members.Any(IsTest)))
{
    var parts=path.Split('/');
    if(parts.Length<4 || parts[1] is not ("Unit" or "Integration")) throw new Exception("Test class outside execution boundary: "+path);
    var traits=type.AttributeLists.SelectMany(a=>a.Attributes).Where(a=>a.Name.ToString()=="Trait").ToList();
    if(traits.Count!=2) throw new Exception("Expected exactly Category and Module traits: "+path);
    foreach(var pair in new[]{("Category",parts[1]),("Module",parts[2])})
    {
        if(traits.Count(a=>a.ArgumentList?.Arguments.Count==2 && a.ArgumentList.Arguments[0].Expression is LiteralExpressionSyntax key && key.Token.ValueText==pair.Item1 && a.ArgumentList.Arguments[1].Expression is LiteralExpressionSyntax value && value.Token.ValueText==pair.Item2)!=1) throw new Exception("Trait does not match path: "+path+":"+pair);
    }
    traitMapping.Add(new{source=path,category=parts[1],module=parts[2],test_methods=type.Members.Count(IsTest)});
}
var operatorBase=newTypes.Single(t=>t.Node.Identifier.ValueText=="OperatorTestBase").Node;
if(operatorBase.Members.Any(IsTest)||operatorBase.BaseList is not null || !operatorBase.Modifiers.Any(t=>t.IsKind(SyntaxKind.AbstractKeyword))) throw new Exception("Operator support must contain preparation only");
var registration=newTypes.Single(t=>t.Node.Identifier.ValueText=="OperatorRegistrationTests").Node;
if(registration.BaseList?.Types.Single().Type.ToString()!="OperatorTestBase" || registration.Members.Count!=1 || MemberName(registration.Members.Single())!="ProductionRegistrationMapsSimulateErrorsIntoProcessing") throw new Exception("Unexpected operator scenario extraction");
Write("csharp-trait-validation.json",traitMapping);
var oldComments = originals.SelectMany(s => Comments(s.Tree)).Order().ToList();
var newComments = finals.SelectMany(s => Comments(s.Tree)).Order().ToList();
if(!oldComments.SequenceEqual(newComments)) throw new Exception("Comments changed. Old only: "+string.Join(";",oldComments.Except(newComments))+" New only: "+string.Join(";",newComments.Except(oldComments))+" Counts "+oldComments.Count+"/"+newComments.Count);
foreach(var source in originals)
{
    var oldUsings=source.Tree.Usings.Select(Tokens).ToHashSet();
    var mapped=finals.Single(s=>Path.GetFileName(s.Path)==Path.GetFileName(source.Path));
    if(!oldUsings.SetEquals(mapped.Tree.Usings.Select(Tokens))) throw new Exception("Scenario source usings changed: "+source.Path);
}

Write("csharp-scenario-mapping.json",tests);
Write("identity-renames.json",rename.Where(v=>v.Key!=v.Value).ToDictionary(v=>v.Key,v=>v.Value));
Write("csharp-member-mapping.json",memberMapping);
Write("csharp-structural-comparison.json",new { parser = "Microsoft.CodeAnalysis.CSharp from installed .NET SDK", source_files = originals.Count,
    original_members = oldMembers.Count, final_members = newMembers.Count, test_methods = oldTests.Count,
    expression_bodied_test_methods = oldTests.Count(t => ((MethodDeclarationSyntax)t.Node).ExpressionBody is not null),
    inline_cases = oldTests.Sum(t=>t.Node.AttributeLists.SelectMany(a=>a.Attributes).Count(a=>a.Name.ToString()=="InlineData")),
    all_leaf_members_equal = true, class_container_changes_allowlisted = true, original_comments_equal = true, original_usings_retained = true,
    permitted_changes = new[]{"four new class identities replacing misleading partial CampaignPausePostgresTests", "Category and Module traits", "Measurement collection on all five derived PostgreSQL scenario classes", "common PostgresTestBase inherits IAsyncLifetime", "private -> private protected only for extracted PostgresTestBase members", "HTTP fixture extracted retaining source", "file paths", "one operator registration scenario extracted into Integration, sharing Config/SequenceRandom through preparation-only OperatorTestBase"} });
Console.WriteLine($"Preserved {oldTests.Count} test methods, {oldMembers.Count} total members; {oldTests.Count(t=>((MethodDeclarationSyntax)t.Node).ExpressionBody is not null)} expression-bodied tests.");
void Write(string file, object value) => File.WriteAllText(Path.Combine(evidence,file),JsonSerializer.Serialize(value,options)+"\n");
static string Hash(string text)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
static IEnumerable<Member> Members(Source source) => source.Tree.DescendantNodes().OfType<MemberDeclarationSyntax>().Where(n => n is not BaseTypeDeclarationSyntax && n is not BaseNamespaceDeclarationSyntax && n is not DelegateDeclarationSyntax).Select(n=>new Member(source.Path,n));
static bool IsTest(MemberDeclarationSyntax node)=>node is MethodDeclarationSyntax && node.AttributeLists.SelectMany(a=>a.Attributes).Any(a=>a.Name.ToString() is "Fact" or "Theory" or "PostgresFact" or "PostgresTheory");
static string Identity(MemberDeclarationSyntax node)=>"PackCall.Core.Test."+node.Ancestors().OfType<TypeDeclarationSyntax>().First().Identifier.ValueText+"."+((MethodDeclarationSyntax)node).Identifier.ValueText;
static bool AllowedLocation(Member original,Member updated)
{
    var oldOwner=original.Node.Ancestors().OfType<TypeDeclarationSyntax>().First().Identifier.ValueText;
    var newOwner=updated.Node.Ancestors().OfType<TypeDeclarationSyntax>().First().Identifier.ValueText;
    var expectedOwner=oldOwner=="CampaignPausePostgresTests" ? Path.GetFileNameWithoutExtension(original.Source) : oldOwner;
    if(expectedOwner==newOwner && Path.GetFileName(original.Source)==Path.GetFileName(updated.Source)) return true;
    if(oldOwner=="TestOperatorTests" && MemberName(original.Node)=="ProductionRegistrationMapsSimulateErrorsIntoProcessing" && newOwner=="OperatorRegistrationTests" && updated.Source=="PackCall.Test/Integration/Operators/OperatorRegistrationTests.cs") return true;
    if(oldOwner=="TestOperatorTests" && MemberName(original.Node)=="Config" && newOwner=="OperatorTestBase" && updated.Source=="PackCall.Test/Support/Operators/OperatorTestBase.cs") return true;
    if(oldOwner=="SequenceRandom" && newOwner=="SequenceRandom" && updated.Source=="PackCall.Test/Support/Operators/OperatorTestBase.cs") return true;
    if(oldOwner=="OperatorServer" && newOwner=="OperatorServer" && updated.Source=="PackCall.Test/Integration/Support/OperatorServer.cs") return true;
    var sharedNames=new[]{"_schema","_sender","_provider","_connectionString","_schemaCreated","InitializeAsync","DisposeAsync","AddServices","CreateStartedCampaignAsync","Timeout","Result","ReadDelivery","Snapshot","Journal","AssertTerminal","DatabaseNow","WaitBlocked"};
    return oldOwner=="CampaignPausePostgresTests" && newOwner=="PostgresTestBase" && updated.Source=="PackCall.Test/Integration/Support/PostgresTestBase.cs" && sharedNames.Contains(MemberName(original.Node));
}
static string Canonical(Member member)
{
    var node=member.Node;
    if((member.Source=="PackCall.Test/Integration/Support/PostgresTestBase.cs" || member.Source=="PackCall.Test/Support/Operators/OperatorTestBase.cs" && MemberName(node)=="Config") && node.Modifiers().Any(t=>t.IsKind(SyntaxKind.PrivateKeyword)) && node.Modifiers().Any(t=>t.IsKind(SyntaxKind.ProtectedKeyword)))
        return string.Join(" ",node.DescendantTokens().Where(t=>!t.IsKind(SyntaxKind.ProtectedKeyword)).Select(t=>t.Text));
    return Tokens(node);
}
static string Tokens(SyntaxNode node)=>string.Join(" ",node.DescendantTokens().Select(t=>t.Text));
static IEnumerable<string> Comments(CompilationUnitSyntax tree)=>tree.DescendantTrivia().Where(t=>t.IsKind(SyntaxKind.SingleLineCommentTrivia)||t.IsKind(SyntaxKind.MultiLineCommentTrivia)||t.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia)||t.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia)).Select(t=>t.ToString().Replace("\r\n","\n"));
static string MemberName(MemberDeclarationSyntax node)=>node switch { MethodDeclarationSyntax m=>m.Identifier.ValueText, ConstructorDeclarationSyntax c=>c.Identifier.ValueText, FieldDeclarationSyntax f=>string.Join(",",f.Declaration.Variables.Select(v=>v.Identifier.ValueText)), PropertyDeclarationSyntax p=>p.Identifier.ValueText, _=>node.Kind().ToString() };
record Source(string Path,string Text) { public CompilationUnitSyntax Tree { get; }=CSharpSyntaxTree.ParseText(Text).GetCompilationUnitRoot(); }
record Member(string Source,MemberDeclarationSyntax Node);
static class MemberExtensions
{
    public static SyntaxTokenList Modifiers(this MemberDeclarationSyntax node)=>node switch { BaseMethodDeclarationSyntax m=>m.Modifiers, BaseFieldDeclarationSyntax f=>f.Modifiers, BasePropertyDeclarationSyntax p=>p.Modifiers, _=>default };
}
