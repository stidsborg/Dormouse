namespace Dormouse.Tests;

using System;
using System.Linq;

[TestClass]
public sealed class ByteArrayMarshallerTests
{
    [TestMethod]
    public void MultipleStringsCanBeMarshalledAndReconstructed()
    {
        const string str1 = "hello world";
        const string str2 = "hello universe";
        const string str3 = "testing tester";
        const string str4 = "";
        byte[]? str5 = null;
        const string str6 = "|";

        var marshalled = ByteArrayMarshaller.Serialize(
            str1.ToUtf8Bytes(), str2.ToUtf8Bytes(), str3.ToUtf8Bytes(), str4.ToUtf8Bytes(), str5, str6.ToUtf8Bytes());
        var reconstructed = ByteArrayMarshaller.Deserialize(marshalled);

        Assert.HasCount(6, reconstructed);
        Assert.AreEqual(str1, reconstructed[0]!.Value.ToStringFromUtf8Bytes());
        Assert.AreEqual(str2, reconstructed[1]!.Value.ToStringFromUtf8Bytes());
        Assert.AreEqual(str3, reconstructed[2]!.Value.ToStringFromUtf8Bytes());
        Assert.AreEqual(str4, reconstructed[3]!.Value.ToStringFromUtf8Bytes());
        Assert.IsNull(reconstructed[4]);
        Assert.AreEqual(str6, reconstructed[5]!.Value.ToStringFromUtf8Bytes());
    }

    [TestMethod]
    public void NothingCanBeMarshalledAndReconstructed()
    {
        var serialized = ByteArrayMarshaller.Serialize();

        Assert.IsEmpty(serialized);
        Assert.IsEmpty(ByteArrayMarshaller.Deserialize(serialized));
    }

    [TestMethod]
    public void EmptyArrayAndNullAreDistinguishedFromEachOther()
    {
        // The -1 length marker is what keeps these apart; collapsing them would silently turn an
        // absent value into a present-but-empty one on the way back out.
        var reconstructed = ByteArrayMarshaller.Deserialize(ByteArrayMarshaller.Serialize(null, [], null, []));

        Assert.HasCount(4, reconstructed);
        Assert.IsNull(reconstructed[0]);
        Assert.AreEqual(0, reconstructed[1]!.Value.Length);
        Assert.IsNull(reconstructed[2]);
        Assert.AreEqual(0, reconstructed[3]!.Value.Length);
    }

    [TestMethod]
    public void ArbitraryBinaryContentIsNotMistakenForFraming()
    {
        // 0xFF x4 is the little-endian encoding of -1, the null marker. As payload it must survive,
        // because lengths are read from known offsets rather than scanned for.
        byte[] looksLikeNullMarker = [0xFF, 0xFF, 0xFF, 0xFF];
        byte[] looksLikeLengthPrefix = [0x04, 0x00, 0x00, 0x00, 0xAB];

        var reconstructed = ByteArrayMarshaller.Deserialize(
            ByteArrayMarshaller.Serialize(looksLikeNullMarker, looksLikeLengthPrefix));

        Assert.HasCount(2, reconstructed);
        CollectionAssert.AreEqual(looksLikeNullMarker, reconstructed[0]!.Value.ToArray());
        CollectionAssert.AreEqual(looksLikeLengthPrefix, reconstructed[1]!.Value.ToArray());
    }

    [TestMethod]
    public void ArraysLongerThanASingleLengthByteRoundTrip()
    {
        var large = Enumerable.Range(0, 100_000).Select(n => (byte)n).ToArray();

        var reconstructed = ByteArrayMarshaller.Deserialize(ByteArrayMarshaller.Serialize(large, [7]));

        Assert.HasCount(2, reconstructed);
        CollectionAssert.AreEqual(large, reconstructed[0]!.Value.ToArray());
        CollectionAssert.AreEqual(new byte[] { 7 }, reconstructed[1]!.Value.ToArray());
    }

    [TestMethod]
    public void SerializedLengthIsFourBytesOfFramingPerArray()
    {
        Assert.HasCount(4, ByteArrayMarshaller.Serialize((byte[]?)null));
        Assert.HasCount(4, ByteArrayMarshaller.Serialize(Array.Empty<byte>()));
        Assert.HasCount(4 + 3, ByteArrayMarshaller.Serialize([1, 2, 3]));
        Assert.HasCount(4 + 3 + 4 + 4 + 1, ByteArrayMarshaller.Serialize([1, 2, 3], null, [9]));
    }

    [TestMethod]
    public void ALoneNullOrEmptyArgumentBindsToTheParamsArrayItselfRatherThanToOneElement()
    {
        // Trap, not a feature. With exactly one argument, both null and [] are convertible to the
        // params array type itself, so overload resolution prefers that over wrapping them in a
        // one-element array. Two or more arguments force the expanded form and behave as expected
        // (see EmptyArrayAndNullAreDistinguishedFromEachOther); only the single-argument call bites.

        // null reaches the foreach as a null params array.
        Assert.ThrowsExactly<NullReferenceException>(() => ByteArrayMarshaller.Serialize(null!));

        // [] is the quieter half: no exception, it just silently means "serialize nothing at all".
        Assert.IsEmpty(ByteArrayMarshaller.Serialize([]));

        // Casting is what makes either one mean "a single array" - as used in the framing test above.
        Assert.HasCount(4, ByteArrayMarshaller.Serialize((byte[]?)null));
        Assert.HasCount(4, ByteArrayMarshaller.Serialize((byte[])[]));
    }

    [TestMethod]
    public void TwoSerializationParsesWorks()
    {
        const string str1 = "hello world";
        const string str2 = "hello universe";
        const string str3 = "testing tester";
        const string str4 = "";
        byte[]? str5 = null;
        const string str6 = "|";

        var marshalled1 = ByteArrayMarshaller.Serialize(str1.ToUtf8Bytes(), str2.ToUtf8Bytes());
        var marshalled2 = ByteArrayMarshaller.Serialize(str3.ToUtf8Bytes(), str4.ToUtf8Bytes(), str5, str6.ToUtf8Bytes());
        var marshalled = ByteArrayMarshaller.Serialize(marshalled1, marshalled2);

        var reconstructed = ByteArrayMarshaller.Deserialize(marshalled);
        Assert.HasCount(2, reconstructed);

        var reconstructed1 = ByteArrayMarshaller.Deserialize(reconstructed[0]!.Value.ToArray());
        var reconstructed2 = ByteArrayMarshaller.Deserialize(reconstructed[1]!.Value.ToArray());

        Assert.HasCount(2, reconstructed1);
        Assert.AreEqual(str1, reconstructed1[0]!.Value.ToStringFromUtf8Bytes());
        Assert.AreEqual(str2, reconstructed1[1]!.Value.ToStringFromUtf8Bytes());

        Assert.HasCount(4, reconstructed2);
        Assert.AreEqual(str3, reconstructed2[0]!.Value.ToStringFromUtf8Bytes());
        Assert.AreEqual(str4, reconstructed2[1]!.Value.ToStringFromUtf8Bytes());
        Assert.IsNull(reconstructed2[2]);
        Assert.AreEqual(str6, reconstructed2[3]!.Value.ToStringFromUtf8Bytes());
    }

    [TestMethod]
    public void ExpectedCountOnlySizesTheResultListAndDoesNotConstrainIt()
    {
        var marshalled = ByteArrayMarshaller.Serialize([1], [2], [3]);

        // Under-, over- and un-specified all yield the same three entries.
        Assert.HasCount(3, ByteArrayMarshaller.Deserialize(marshalled, expectedCount: 1));
        Assert.HasCount(3, ByteArrayMarshaller.Deserialize(marshalled, expectedCount: 99));
        Assert.HasCount(3, ByteArrayMarshaller.Deserialize(marshalled));
    }

    [TestMethod]
    public void DeserializedSegmentsAreViewsOverTheSourceBufferRatherThanCopies()
    {
        var marshalled = ByteArrayMarshaller.Serialize([1, 2, 3]);
        var segment = ByteArrayMarshaller.Deserialize(marshalled)[0]!.Value;

        // Zero-copy by design: mutating the marshalled buffer is visible through the segment, so
        // callers must not reuse or overwrite a buffer they have handed to Deserialize.
        marshalled[^1] = 42;

        CollectionAssert.AreEqual(new byte[] { 1, 2, 42 }, segment.ToArray());
    }

    [TestMethod]
    public void DeserializeThrowsOnATruncatedBuffer()
    {
        var marshalled = ByteArrayMarshaller.Serialize([1, 2, 3]);

        // Framing is trusted, not validated - a short read runs off the end rather than reporting
        // a malformed payload. Worth knowing before pointing this at untrusted input.
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => ByteArrayMarshaller.Deserialize(marshalled[..5]));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => ByteArrayMarshaller.Deserialize(marshalled[..2]));
    }
}
