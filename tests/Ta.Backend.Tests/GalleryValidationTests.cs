using System.Buffers.Binary;
using Ta.Backend.Features.Biometrics;

namespace Ta.Backend.Tests;

public sealed class GalleryValidationTests
{
    [Fact]
    public void Identifiers_respect_the_edges_utf8_byte_limit()
    {
        Assert.True(Ta.Backend.Common.WireJson.Identifier(new string('a', 256)));
        Assert.False(Ta.Backend.Common.WireJson.Identifier(new string('\u5b66', 86)));
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void Invalid_embedding_is_rejected(float value)
    {
        var bytes = new byte[2048];
        BinaryPrimitives.WriteSingleLittleEndian(bytes, value);
        var hash = new string('a', 64);
        var document = new GalleryDocument(1, "v1", "insightface/w600k_r50", hash, 512, "f32le-base64",
            [new GalleryTemplate("t1", "i1", 512, "f32le-base64", Convert.ToBase64String(bytes))]);
        Assert.NotNull(document.Validate(hash));
    }

    [Fact]
    public void Wrong_model_and_duplicate_templates_are_rejected()
    {
        var bytes = new byte[2048];
        BinaryPrimitives.WriteSingleLittleEndian(bytes, 1f);
        var hash = new string('a', 64);
        var template = new GalleryTemplate("t1", "i1", 512, "f32le-base64", Convert.ToBase64String(bytes));
        var document = new GalleryDocument(1, "v1", "insightface/w600k_r50", hash, 512, "f32le-base64", [template]);
        Assert.NotNull(document.Validate(new string('b', 64)));
        Assert.NotNull((document with { Templates = [template, template] }).Validate(hash));
    }
}
