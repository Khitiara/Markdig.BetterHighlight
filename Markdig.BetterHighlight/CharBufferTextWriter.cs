// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft;
using Nerdbank.Streams;

namespace Markdig.BetterHighlight;

/// <summary>
/// A <see cref="TextWriter"/> that writes to a reassignable instance of <see cref="IBufferWriter{T}"/>.
/// </summary>
/// <remarks>
/// Using this is much more memory efficient than a <see cref="StreamWriter"/> when writing to many different
/// <see cref="IBufferWriter{T}"/> because the same writer, with all its buffers, can be reused.
/// </remarks>
public class CharBufferTextWriter : TextWriter
{
    /// <summary>
    /// The internal buffer writer to use for writing encoded characters.
    /// </summary>
    private IBufferWriter<char>? _bufferWriter;

    /// <summary>
    /// Initializes a new instance of the <see cref="BufferTextWriter"/> class.
    /// </summary>
    /// <remarks>
    /// When using this constructor, call <see cref="Initialize(IBufferWriter{char})"/>
    /// to associate the instance with the initial writer to use before using any write or flush methods.
    /// </remarks>
    public CharBufferTextWriter() { }

    /// <summary>
    /// Initializes a new instance of the <see cref="BufferTextWriter"/> class.
    /// </summary>
    /// <param name="bufferWriter">The buffer writer to write to.</param>
    public CharBufferTextWriter(IBufferWriter<char> bufferWriter) {
        Initialize(bufferWriter);
    }

    /// <inheritdoc />
    public override Encoding Encoding => Encoding.Unicode;

    /// <summary>
    /// Prepares for writing to the specified buffer.
    /// </summary>
    /// <param name="bufferWriter">The buffer writer to write to.</param>
    public void Initialize(IBufferWriter<char> bufferWriter) {
        ArgumentNullException.ThrowIfNull(bufferWriter);
        _bufferWriter = bufferWriter;
    }

    /// <summary>
    /// Clears references to the <see cref="IBufferWriter{T}"/> set by a prior call to <see cref="Initialize(IBufferWriter{char})"/>.
    /// </summary>
    public void Reset() {
        _bufferWriter = null;
    }

    /// <inheritdoc />
    public override void Flush() {
        ThrowIfNotInitialized();
    }

    /// <inheritdoc />
    public override Task FlushAsync() {
        try {
            Flush();
            return Task.CompletedTask;
        }
        catch (Exception ex) {
            return Task.FromException(ex);
        }
    }

    /// <inheritdoc />
    public override void Write(char value) {
        ThrowIfNotInitialized();
        _bufferWriter.Write(stackalloc[] { value, });
    }

    /// <inheritdoc />
    public override void Write(string? value) {
        if (value == null) {
            return;
        }

        Write(value.AsSpan());
    }

    /// <inheritdoc />
    public override void Write(char[] buffer, int index, int count) =>
        Write(Requires.NotNull(buffer, nameof(buffer)).AsSpan(index, count));


    /// <inheritdoc />
    public override void Write(ReadOnlySpan<char> buffer) {
        ThrowIfNotInitialized();

        // Try for fast path
        _bufferWriter.Write(buffer);
    }

    /// <inheritdoc />
    public override void WriteLine(ReadOnlySpan<char> buffer) {
        Write(buffer);
        WriteLine();
    }

    public void WriteLine(ref DefaultInterpolatedStringHandler handler) {
        Write(ref handler);
        WriteLine();
    }

    public void Write(ref DefaultInterpolatedStringHandler handler) {
        Write(GetText(handler));
        Clear(handler);

        [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_Text")]
        static extern ReadOnlySpan<char> GetText(DefaultInterpolatedStringHandler handler);

        [UnsafeAccessor(UnsafeAccessorKind.Method)]
        static extern void Clear(DefaultInterpolatedStringHandler handler);
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing) {
        if (disposing) {
            if (_bufferWriter is not null) {
                Flush();
            }
        }

        base.Dispose(disposing);
    }

    [MemberNotNull(nameof(_bufferWriter))]
    private void ThrowIfNotInitialized() {
        if (_bufferWriter == null) {
            throw new InvalidOperationException("Call " + nameof(Initialize) + " first.");
        }
    }
}