namespace Dormouse.Tests;

using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using Dormouse.Messages;

[TestClass]
public sealed class TypeHelperTests
{
    private readonly TypeHelper Types = new();

    [TestMethod]
    public void SimpleQualifiedNameKeepsTypeAndAssemblyButDropsVersionCultureAndToken()
    {
        Assert.AreEqual("System.String, System.Private.CoreLib", Types.SimpleQualifiedName(typeof(string)));
        Assert.AreEqual("Dormouse.Messages.StartOrder, Dormouse", Types.SimpleQualifiedName(typeof(StartOrder)));
    }

    [TestMethod]
    public void SimpleQualifiedNameSimplifiesEveryGenericArgumentScope()
    {
        Assert.AreEqual(
            "System.Collections.Generic.List`1[[System.String, System.Private.CoreLib]], System.Private.CoreLib",
            Types.SimpleQualifiedName(typeof(List<string>)));

        // Two arguments: the "],[" separator is not an assembly qualifier and must survive.
        Assert.AreEqual(
            "System.Collections.Generic.Dictionary`2[" +
            "[System.String, System.Private.CoreLib],[System.Int32, System.Private.CoreLib]], System.Private.CoreLib",
            Types.SimpleQualifiedName(typeof(Dictionary<string, int>)));

        // Nesting: the inner List`1 scope is simplified as well as the outer one.
        Assert.AreEqual(
            "System.Collections.Generic.List`1[" +
            "[System.Collections.Generic.List`1[[System.String, System.Private.CoreLib]], System.Private.CoreLib]], " +
            "System.Private.CoreLib",
            Types.SimpleQualifiedName(typeof(List<List<string>>)));
    }

    [TestMethod]
    public void SimpleQualifiedNamePreservesArraySuffixes()
    {
        Assert.AreEqual("System.String[], System.Private.CoreLib", Types.SimpleQualifiedName(typeof(string[])));

        // The ',' inside "[,]" is a rank separator, not an assembly qualifier.
        Assert.AreEqual("System.Int32[,], System.Private.CoreLib", Types.SimpleQualifiedName(typeof(int[,])));

        Assert.AreEqual(
            "System.Collections.Generic.List`1[[System.String, System.Private.CoreLib]][], System.Private.CoreLib",
            Types.SimpleQualifiedName(typeof(List<string>[])));
    }

    [TestMethod]
    public void SimpleQualifiedNameIsCached()
    {
        // A cache hit hands back the very same string instance rather than rebuilding it.
        Assert.AreSame(Types.SimpleQualifiedName(typeof(PaymentReceived)), Types.SimpleQualifiedName(typeof(PaymentReceived)));
    }

    [TestMethod]
    public void EachHelperOwnsItsCache()
    {
        // Two helpers agree on the name but each builds its own: nothing is shared process-wide,
        // so a context's caches are its own and a test's helper starts empty.
        var name = Types.SimpleQualifiedName(typeof(PaymentReceived));
        var other = new DormouseContext().TypeHelper.SimpleQualifiedName(typeof(PaymentReceived));

        Assert.AreEqual(name, other);
        Assert.AreNotSame(name, other);
    }

    [TestMethod]
    public void SimpleQualifiedNameRefusesATypeWhoseNameHasToBeEscaped()
    {
        // Reflection.Emit is the reachable way to get a metadata name C# cannot spell. The comma comes
        // back from reflection escaped as "Ns.My\,Type", and the walk would read it as the assembly
        // qualifier and drop ", EscapedNameAssembly" - leaving a name that no longer resolves.
        var assembly = AssemblyBuilder.DefineDynamicAssembly(
            new AssemblyName("EscapedNameAssembly"), AssemblyBuilderAccess.Run);
        var type = assembly
            .DefineDynamicModule("EscapedNameModule")
            .DefineType("Ns.My,Type", TypeAttributes.Public)
            .CreateType();

        Assert.ThrowsExactly<ArgumentException>(() => Types.SimpleQualifiedName(type));

        // The second call matters: the refusal must not have been cached as a value either way.
        Assert.ThrowsExactly<ArgumentException>(() => Types.SimpleQualifiedName(type));
    }

    [DataRow(typeof(string))]
    [DataRow(typeof(int))]
    [DataRow(typeof(StartOrder))]
    [DataRow(typeof(OrderShipped))]
    [DataRow(typeof(string[]))]
    [DataRow(typeof(int[,]))]
    [DataRow(typeof(List<string>))]
    [DataRow(typeof(Dictionary<string, StartOrder>))]
    [DataRow(typeof(List<List<StartOrder>>))]
    [DataRow(typeof(List<string>[]))]
    [TestMethod]
    public void SerializeTypeRoundTripsThroughResolveType(Type type)
        => Assert.AreEqual(type, Types.ResolveType(Types.SerializeType(type)));

    [TestMethod]
    public void ResolveTypeMatchesOnNameContentNotByteArrayIdentity()
    {
        // Two separate arrays holding the same name must resolve alike, which is why the cache keys
        // on the decoded string - keying on the bytes themselves would compare by reference.
        var first = Types.SerializeType(typeof(OrderCompleted));
        var second = Types.SerializeType(typeof(OrderCompleted));

        Assert.AreNotSame(first, second);
        Assert.AreEqual(typeof(OrderCompleted), Types.ResolveType(first));
        Assert.AreEqual(typeof(OrderCompleted), Types.ResolveType(second));
    }

    [TestMethod]
    public void ResolveTypeKeepsThrowingForAnUnresolvableNameRatherThanCachingTheFailure()
    {
        var unresolvable = "Dormouse.NoSuchTypeExists, Dormouse".ToUtf8Bytes();

        // The second call matters: if the failure were cached as null, it would stop throwing.
        Assert.ThrowsExactly<TypeLoadException>(() => Types.ResolveType(unresolvable));
        Assert.ThrowsExactly<TypeLoadException>(() => Types.ResolveType(unresolvable));
    }

    [TestMethod]
    public void Utf8BytesRoundTripThroughText()
    {
        const string text = "Dormouse.Messages.StartOrder, Dormouse";

        Assert.AreEqual(text, text.ToUtf8Bytes().ToStringFromUtf8Bytes());
        CollectionAssert.AreEqual(Encoding.UTF8.GetBytes(text), text.ToUtf8Bytes());
    }
}
