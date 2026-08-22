namespace Dormouse.Tests;

using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using Dormouse.Messages;

[TestClass]
public sealed class TypeHelperTests
{
    [TestMethod]
    public void SimpleQualifiedNameKeepsTypeAndAssemblyButDropsVersionCultureAndToken()
    {
        Assert.AreEqual("System.String, System.Private.CoreLib", typeof(string).SimpleQualifiedName());
        Assert.AreEqual("Dormouse.Messages.StartOrder, Dormouse", typeof(StartOrder).SimpleQualifiedName());
    }

    [TestMethod]
    public void SimpleQualifiedNameSimplifiesEveryGenericArgumentScope()
    {
        Assert.AreEqual(
            "System.Collections.Generic.List`1[[System.String, System.Private.CoreLib]], System.Private.CoreLib",
            typeof(List<string>).SimpleQualifiedName());

        // Two arguments: the "],[" separator is not an assembly qualifier and must survive.
        Assert.AreEqual(
            "System.Collections.Generic.Dictionary`2[" +
            "[System.String, System.Private.CoreLib],[System.Int32, System.Private.CoreLib]], System.Private.CoreLib",
            typeof(Dictionary<string, int>).SimpleQualifiedName());

        // Nesting: the inner List`1 scope is simplified as well as the outer one.
        Assert.AreEqual(
            "System.Collections.Generic.List`1[" +
            "[System.Collections.Generic.List`1[[System.String, System.Private.CoreLib]], System.Private.CoreLib]], " +
            "System.Private.CoreLib",
            typeof(List<List<string>>).SimpleQualifiedName());
    }

    [TestMethod]
    public void SimpleQualifiedNamePreservesArraySuffixes()
    {
        Assert.AreEqual("System.String[], System.Private.CoreLib", typeof(string[]).SimpleQualifiedName());

        // The ',' inside "[,]" is a rank separator, not an assembly qualifier.
        Assert.AreEqual("System.Int32[,], System.Private.CoreLib", typeof(int[,]).SimpleQualifiedName());

        Assert.AreEqual(
            "System.Collections.Generic.List`1[[System.String, System.Private.CoreLib]][], System.Private.CoreLib",
            typeof(List<string>[]).SimpleQualifiedName());
    }

    [TestMethod]
    public void SimpleQualifiedNameIsCached()
    {
        // A cache hit hands back the very same string instance rather than rebuilding it.
        Assert.AreSame(typeof(PaymentReceived).SimpleQualifiedName(), typeof(PaymentReceived).SimpleQualifiedName());
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

        Assert.ThrowsExactly<ArgumentException>(() => type.SimpleQualifiedName());

        // The second call matters: the refusal must not have been cached as a value either way.
        Assert.ThrowsExactly<ArgumentException>(() => type.SimpleQualifiedName());
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
        => Assert.AreEqual(type, type.SerializeType().ResolveType());

    [TestMethod]
    public void ResolveTypeMatchesOnNameContentNotByteArrayIdentity()
    {
        // Two separate arrays holding the same name must resolve alike, which is why the cache keys
        // on the decoded string - keying on the byte[] itself would compare by reference.
        var first = typeof(OrderCompleted).SerializeType();
        var second = typeof(OrderCompleted).SerializeType();

        Assert.AreNotSame(first, second);
        Assert.AreEqual(typeof(OrderCompleted), first.ResolveType());
        Assert.AreEqual(typeof(OrderCompleted), second.ResolveType());
    }

    [TestMethod]
    public void ResolveTypeKeepsThrowingForAnUnresolvableNameRatherThanCachingTheFailure()
    {
        var unresolvable = "Dormouse.NoSuchTypeExists, Dormouse".ToUtf8Bytes();

        // The second call matters: if the failure were cached as null, it would stop throwing.
        Assert.ThrowsExactly<TypeLoadException>(() => unresolvable.ResolveType());
        Assert.ThrowsExactly<TypeLoadException>(() => unresolvable.ResolveType());
    }

    [TestMethod]
    public void Utf8BytesRoundTripThroughText()
    {
        const string text = "Dormouse.Messages.StartOrder, Dormouse";

        Assert.AreEqual(text, text.ToUtf8Bytes().ToStringFromUtf8Bytes());
        CollectionAssert.AreEqual(Encoding.UTF8.GetBytes(text), text.ToUtf8Bytes());
    }
}
