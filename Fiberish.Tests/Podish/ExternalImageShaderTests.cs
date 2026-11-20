using System.Text;
using Podish.Core.Graphics;
using Xunit;

namespace Fiberish.Tests.Podish;

public sealed class ExternalImageShaderTests
{
    private const string Extension = "#extension GL_OES_EGL_image_external : require";
    private const string Precision = """
        #ifdef GL_FRAGMENT_PRECISION_HIGH
        precision highp float;
        #else
        precision mediump float;
        #endif
        """;

    [Fact]
    public void Rewrite_PreservesFragmentPrecisionConditional()
    {
        var source = Encoding.UTF8.GetBytes($$"""
            {{Extension}}
            {{Precision}}
            varying vec2 v_texcoord;
            uniform samplerExternalOES texture0;
            void main() { gl_FragColor = texture2D(texture0, v_texcoord); }
            """);

        Assert.True(ExternalImageShader.TryRewrite(source, out var rewritten, out var uniforms));
        Assert.Equal(new[] { "texture0" }, uniforms);
        Assert.NotNull(rewritten);
        Assert.Equal(source.Length, rewritten.Length);
        var text = Encoding.UTF8.GetString(rewritten);
        Assert.Contains(Precision, text);
        Assert.Contains("uniform sampler2D", text);
        Assert.DoesNotContain(Extension, text);
        Assert.DoesNotContain("samplerExternalOES", text);
    }

    [Theory]
    [InlineData("uniform samplerExternalOES branch_texture;")]
    [InlineData("#define samplerExternalOES sampler2D")]
    public void Rewrite_LeavesBranchesThatCanAffectUniformsAlone(string branch)
    {
        var source = Encoding.UTF8.GetBytes(Extension + "\n" +
            Precision.Replace("#else\n", "#else\n" + branch + "\n", StringComparison.Ordinal) +
            "\nuniform samplerExternalOES texture0;");

        Assert.False(ExternalImageShader.TryRewrite(source, out var rewritten, out var uniforms));
        Assert.Null(rewritten);
        Assert.Null(uniforms);
    }
}
