using System.Formats.Cbor;
using Xunit;

namespace Ddd.Windows.Tests;

// Candidate-library probes only: these are not the DDD wire schema or its validation rules.
public class CborFeasibilityTests
{
    [Fact]
    public void ByteStringsPreserveNonTextPayload()
    {
        byte[] payload = [0, 0xff, 13, 10];
        var writer = new CborWriter(CborConformanceMode.Strict);
        writer.WriteByteString(payload);
        Assert.Equal(new byte[] { 0x44, 0, 0xff, 13, 10 }, writer.Encode());
        var reader = new CborReader(writer.Encode(), CborConformanceMode.Strict);
        Assert.Equal(payload, reader.ReadByteString());
        Assert.Equal(0, reader.BytesRemaining);
    }

    [Fact]
    public void StrictModeRejectsDuplicateMapKeys()
    {
        var reader = new CborReader(new byte[] { 0xa2, 0x01, 0x01, 0x01, 0x02 }, CborConformanceMode.Strict);
        Assert.Equal(2, reader.ReadStartMap());
        Assert.Equal(1, reader.ReadInt32());
        Assert.Equal(1, reader.ReadInt32());
        Assert.Throws<CborContentException>(() => reader.ReadInt32());
    }

    [Theory]
    [InlineData("44FF")]
    [InlineData("5BFFFFFFFFFFFFFFFF")]
    public void TruncatedAndImpossibleLengthsAreRejected(string hex)
    {
        var reader = new CborReader(Convert.FromHexString(hex), CborConformanceMode.Strict);
        Assert.Throws<CborContentException>(() => reader.ReadByteString());
    }

    [Fact]
    public void TrailingDataMustBeExplicitlyCheckedByTheFutureCodec()
    {
        var reader = new CborReader(new byte[] { 0x01, 0x02 }, CborConformanceMode.Strict, allowMultipleRootLevelValues: true);
        Assert.Equal(1, reader.ReadInt32());
        Assert.Equal(1, reader.BytesRemaining);
    }
}
