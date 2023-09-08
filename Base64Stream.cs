//=============================================================================
// License:
// This is free and unencumbered software released into the public domain.
// Anyone is free to copy, modify, publish, use, compile, sell, or distribute 
// this software, either in source code form or as a compiled binary, for any 
// purpose, commercial or non-commercial, and by any means.
// ----------------------------------------------------------------------------
// Disclaimer: 
// This code is provided as-is, with no warranties or guarantees of any kind. 
// Use at your own risk. 
//=============================================================================
using System;
using System.IO;


/// <summary>
/// A two-way stream which encodes or decodes base64.
/// </summary>
///
/// <remarks>
///
/// There wasn't a Base64Stream class in .NET, so I created one.
///
/// This stream does not support consistency of data between reads and writes,
/// as the input and output uses separate buffers.
///
/// Needs C# 7.0+ for switch with 0b0010101 binary literal cases.
///
/// </remarks>
public class Base64Stream : Stream {



    #region Public Variables

    /// <summary>
    /// Customizable ASCII codepoint for base64 codepoint 62.  
    /// Normally, a '+' is used.
    /// </summary>
    public byte Byte62 = 43; // '+'

    /// <summary>
    /// Customizable ASCII codepoint for base64 codepoint 63.  
    /// Normally it's a '/' is used.
    /// </summary>
    public byte Byte63 = 47; // '/'

    /// <summary>
    /// Customizable ASCII codepoint used to pad the ending of the base64 stream.
    /// </summary>
    public byte PadWithByte = 61; // '='

    /// <summary>
    /// The output line length of the Base64 (needed for some old SMTP servers).
    /// A CRLF "\r\n" will be appended after this length is reached, if it is
    /// non-zero.
    /// </summary>
    /// <remarks>
    /// Should be a multiple of 4, or else the first multiple of 4 surpassing
    /// this length will be used.
    /// </remarks>
    public int OutputLineLength = 0;//72;

    #endregion



    #region Internal Variables

    /// <summary>
    /// The number of bytes output since the last CRLF newline.
    /// </summary>
    /// <remarks>
    /// Only used if <c>OutputLineLength</c> is greater than zero.
    /// </remarks>
    private int outputLineCurrentSize = 0;

    /// <summary>
    /// The inner stream to wrap around
    /// </summary>
    private Stream wrapped;

    /// <summary>
    /// Whether to leave the underlying stream open or not after disposal.
    /// Could be useful for, like writing base64 to an excessively long
    /// email stream.
    /// </summary>
    private bool leaveOpen;

    /// <summary>
    /// The number of bytes in the internal input buffer.
    /// Initialize to zero.  -1 is end of stream.
    /// </summary>
    private int inputBufferSize = 0;

    /// <summary>
    /// The current read index in the internal input buffer
    /// </summary>
    private int inputBufferOffset = 0;

    /// <summary>
    /// The internal input buffer
    /// </summary>
    private byte[] inputBuffer = null;

    /// <summary>
    /// The internal output buffer
    /// </summary>
    private byte[] outputBuffer = null;

    /// <summary>
    /// The number of bytes in the internal output buffer.
    /// </summary>
    private int outputBufferSize = 0;

    /// <summary>
    /// The internal bit buffer which stores up to 24 bits of data
    /// for conversion between three 8-bit binary bytes 
    /// and four 6-bit base64 bytes.  This one is used for output,
    /// separately from input.
    /// </summary>
    private int outputBitBuffer = 0;

    /// <summary>
    /// The internal bit buffer which stores up to 24 bits of data
    /// for conversion between three 8-bit binary bytes 
    /// and four 6-bit base64 bytes.  This one is used for output,
    /// separately from input.
    /// </summary>
    private int inputBitBuffer = 0;

    /// <summary>
    /// The offset of which 6-bit chunk into the current bit output buffer
    /// </summary>
    private int outputBitStep = 0; // Which 6-bit chunk is it on [0 - 2]

    /// <summary>
    /// Number of bytes in <c>inputBitBuffer</c>
    /// </summary>
    private int inputBitSize = 0;

    /// <summary>
    /// The current byte offset in <c>inputBitBuffer</c>
    /// </summary>
    private int inputBitStep = 0;

    #endregion



    #region Constructors

    /// <summary>
    /// Creates a new Base64Stream with the specified underlying stream
    /// and internal buffer size.
    /// </summary>
    /// <param name="wrappedStream">The underlying stream consisting of base64 data.</param>
    /// <param name="bufferSize">
    /// The size of the internal buffer used by this stream
    /// before making a read/write call to the underlying stream.
    /// </param>
    public Base64Stream(Stream wrappedStream, int bufferSize = 0, bool leaveOpen = false) {

        if (wrappedStream == null) {
            throw new NullReferenceException("Underlying stream cannot be null");
        }
        this.wrapped = wrappedStream;

        if (bufferSize < 6) {
            // Need at least 6 for the first four 6-bit output bytes and possible CRLF
            bufferSize = 4096;
        }
        this.outputBuffer = new byte[bufferSize];
        this.inputBuffer = new byte[bufferSize];

        this.leaveOpen = leaveOpen;
    }


    /// <summary>
    /// Finalizer
    /// </summary>
    ~Base64Stream() {
        Dispose(false);
    }

    #endregion



    #region Closing

    /// <summary>
    /// We need to flush the bit output here, but the stream cleanup is
    /// done in Dispose().
    /// The .NET docs say:
    /// Do not override the Close() method, instead, 
    /// put all the Stream cleanup logic in the Dispose(Boolean) method. 
    /// </summary>
    public override void Close() {
        try {
            endOutput();
        } catch (Exception x) {
        }
    }


    /// <summary>
    /// Disposes of this stream. 
    /// Close() should be called to send remaining bits of base64 conversion to
    /// the underlying stream, though Close() will not close the underlying stream.
    /// </summary>
    /// <remarks>
    /// Closes this stream and the underlying stream if <c>leaveOpen</c> was false in constructor.
    /// </remarks>
    /// <param name="disposing">Whether the method is called explicitly by user code or as part of the finalization process by the garbage collector.</param>
    protected override void Dispose(bool disposing) {

        if (disposing) {
            try {
                Close();
                if (!leaveOpen && wrapped != null) {
                    wrapped.Close();
                }
                wrapped = null;
            } catch (Exception x) {
            }
        }

        // Null out the internal buffers
        inputBuffer = null;
        outputBuffer = null;

        base.Dispose(disposing);
    }

    #endregion



    #region Seeking

    /// <summary>
    /// Gets a value indicating whether the stream supports seeking.
    /// </summary>
    public override bool CanSeek {
        get {
            if (wrapped == null) {
                return false;
            }
            return wrapped.CanSeek;
        }
    }


    /// <summary>
    /// Gets or sets the position in the stream.
    /// </summary>
    /// <remarks>
    /// Position in base64 encoded stream is inconsistent with position in decoded stream.
    /// </remarks>
    /// <exception cref="NotSupportedException">
    /// Thrown when this method is called.
    /// </exception>
    public override long Position {
        get {
            throw new NotSupportedException("Position in base64 encoded stream is inconsistent with position in decoded stream");
        }
        set {
            throw new NotSupportedException("Position in base64 encoded stream is inconsistent with position in decoded stream");
        }
    }


    /// <summary>
    /// Sets the position in the stream.
    /// </summary>
    /// <remarks>
    /// Position in base64 encoded stream is inconsistent with position in decoded stream.
    /// </remarks>
    /// <param name="offset">The offset from the origin.</param>
    /// <param name="origin">The reference point for the offset.</param>
    /// <returns>The new position in the stream.</returns>
    /// <exception cref="NotSupportedException">
    /// Thrown when this method is called.
    /// </exception>
    public override long Seek(long offset, SeekOrigin origin) {
        throw new NotSupportedException("Position in base64 encoded stream is inconsistent with position in decoded stream");
    }


    /// <summary>
    /// Sets the length of the stream.
    /// </summary>
    /// <remarks>
    /// Length of Base64Stream cannot be set.
    /// </remarks>
    /// <param name="value">The new length of the stream.</param>
    /// <exception cref="NotSupportedException">
    /// Thrown when this method is called.
    /// </exception>
    public override void SetLength(long value) {
        throw new NotSupportedException("Length of Base64Stream cannot be set");
    }


    /// <summary>
    /// Length is not supported by this <c>Stream</c> and should not be used.  
    /// Use <c>HasData()</c> instead.
    /// </summary>
    public override long Length {
        get {
            throw new NotSupportedException("Length of base64 encoded stream is inconsistent with length of decoded stream and encoded stream may contain extra newlines and spaces");
        }
    }
    #endregion



    #region Writing

    /// <summary>
    /// Whether this stream can be written to or not.
    /// </summary>
    public override bool CanWrite {
        get {
            if (wrapped == null) {
                return false;
            }
            return wrapped.CanWrite;
        }
    }


    /// <summary>
    /// Writes the specified byte to this <c>Base64Stream</c>.
    /// Only the eight low-order bits of the argument <c>b</c> will
    /// be encoded to the <c>Base 64 Alphabet</c> and then written
    /// to the underlying <c>Stream</c>.
    /// </summary>
    /// <remarks>
    /// Buffers the 8-bit bytes into an internal int until it has 24 bits, after which
    /// the 24-bit value is splitted into four 6-bit values for output to the base64 alphabet.
    /// </remarks>
    /// <param name="b">The <c>byte</c> to write.  Byte is an immutable value type that represents unsigned integers with values that range from 0 (which is represented by the Byte. MinValue constant) to 255 (which is represented by the Byte. MaxValue constant).</param>
    public override void WriteByte(byte b) {
        if (wrapped == null) {
            throw new ObjectDisposedException("The underlying stream has already been disposed");
        }
        // Three steps, one for each byte going into the 24-bit value buffer
        if (outputBitStep == 0) {
            // At step=0, the out_byte_bits buffer is zero
            outputBitBuffer |= ((b << 16) & 0x00ff0000);
            outputBitStep = 1;
        } else if (outputBitStep == 1) {
            outputBitBuffer |= ((b << 8) & 0x0000ff00);
            outputBitStep = 2;
        } else {
            outputBitBuffer |= (b & 0x000000ff);

            // Check if the output buffer needs to be flushed first
            if (outputBufferSize > outputBuffer.Length - 6) {
                wrapped.Write(outputBuffer, 0, outputBufferSize);
                outputBufferSize = 0;
            }

            // Split the 24-bit 'out_byte_bits' into four 6-bit output bytes...
            outputBuffer[outputBufferSize++] = ToBase64Alphabet((outputBitBuffer >> 18) & 0x0000003f);
            outputBuffer[outputBufferSize++] = ToBase64Alphabet((outputBitBuffer >> 12) & 0x0000003f);
            outputBuffer[outputBufferSize++] = ToBase64Alphabet((outputBitBuffer >> 6) & 0x0000003f);
            outputBuffer[outputBufferSize++] = ToBase64Alphabet(outputBitBuffer & 0x0000003f);

            // Check if we need to append EOL (CRLF)
            if (OutputLineLength > 0) {
                // Then we are counting line size
                outputLineCurrentSize += 4;
                if (outputLineCurrentSize >= OutputLineLength) {
                    outputBuffer[outputBufferSize++] = 13; // (byte)'\r';
                    outputBuffer[outputBufferSize++] = 10; // (byte)'\n';
                    outputLineCurrentSize = 0;
                }
            }

            //
            // Must clear bit buffer and reset the step
            //
            outputBitStep = outputBitBuffer = 0;
        }
    }


    /// <summary>
    /// Encodes and writes the bytes in the specified buffer to the underlying stream.
    /// </summary>
    /// <param name="bufferToWrite">The byte[] array containing the binary data to write to base64.</param>
    /// <param name="writeOffset">The offset into the specified buffer to start writing from.</param>
    /// <param name="writeLength">The number of bytes to write.</param>
    public override void Write(byte[] bufferToWrite, int writeOffset, int writeLength) {

        if (wrapped == null) {
            throw new ObjectDisposedException("The underlying stream has already been disposed");
        }

        int srcOff = writeOffset;
        int srcEnd = writeOffset + writeLength;

        // Tidy-up the bit buffer first, we can skip it when buffers are large enough
        while (outputBitStep != 0 && srcOff < srcEnd) {
            WriteByte(bufferToWrite[srcOff++]);
        }

        // Mark the end before we need to flush
        int dstEnd = outputBuffer.Length - 6;

        // We may have up to 4 bytes left
        int leftoversBytes = (srcEnd - srcOff) % 4;
        srcEnd -= leftoversBytes;
        while (srcOff < srcEnd) {

            // Flush buffer first if needed
            if (outputBufferSize >= dstEnd) {
                wrapped.Write(outputBuffer, 0, outputBufferSize);
                outputBufferSize = 0;
            }

            // Buffer the next three 24 bits to output into an integer
            outputBitBuffer =
                (bufferToWrite[srcOff++] << 16)
                | (bufferToWrite[srcOff++] << 8)
                | (bufferToWrite[srcOff++])
            ;

            // Split the 24-bit 'out_byte_bits' into four 6-bit output bytes...
            outputBuffer[outputBufferSize++] = ToBase64Alphabet((outputBitBuffer >> 18) & 0x0000003f);
            outputBuffer[outputBufferSize++] = ToBase64Alphabet((outputBitBuffer >> 12) & 0x0000003f);
            outputBuffer[outputBufferSize++] = ToBase64Alphabet((outputBitBuffer >> 6) & 0x0000003f);
            outputBuffer[outputBufferSize++] = ToBase64Alphabet(outputBitBuffer & 0x0000003f);

            // Check if we need to append EOL (CRLF)
            if (OutputLineLength > 0) {
                // Then we are counting line size
                outputLineCurrentSize += 4;
                if (outputLineCurrentSize >= OutputLineLength) {
                    outputBuffer[outputBufferSize++] = 13; // (byte)'\r';
                    outputBuffer[outputBufferSize++] = 10; // (byte)'\n';
                    outputLineCurrentSize = 0;
                }
            }

            outputBitBuffer = 0; // Reset to zero
        }

        srcEnd += leftoversBytes; // Restore to original length

        // Output the last few bytes
        while (srcOff < srcEnd) {
            WriteByte(bufferToWrite[srcOff++]);
        }
    }


    /// <summary>
    /// Flushes the internal output buffer of this stream to the underlying stream.
    /// </summary>
    public override void Flush() {
        if (wrapped == null) {
            throw new ObjectDisposedException("The underlying stream has already been disposed");
        }
        if (outputBufferSize > 0) {
            wrapped.Write(outputBuffer, 0, outputBufferSize);
            outputBufferSize = 0;
        }
        wrapped.Flush();
    }


    /// <summary>
    /// Like flush, except it also flushes the remaining bits from the output bit buffer.
    /// </summary>
    /// <remarks>
    /// If <c>PadWithBytes</c> is greater than zero, this will cause
    /// the output to become inconsistent.
    /// Calling this before the data is complete may also cause the data to 
    /// become inconsistent.
    /// Therefore, it should probably only be called internally.
    /// </remarks>
    private void endOutput() {
        // Output any remaining bits
        if (wrapped == null) {
            return;
        }
        if (outputBitStep == 1) {

            // Check if the output buffer needs to be flushed first
            if (PadWithByte > 0) {
                if (outputBufferSize > outputBuffer.Length - 4) {
                    wrapped.Write(outputBuffer, 0, outputBufferSize);
                    outputBufferSize = 0;
                }
            } else if (outputBufferSize > outputBuffer.Length - 2) {
                wrapped.Write(outputBuffer, 0, outputBufferSize);
                outputBufferSize = 0;
            }

            outputBuffer[outputBufferSize++] = ToBase64Alphabet((outputBitBuffer >> 18) & 0x0000003f);
            outputBuffer[outputBufferSize++] = ToBase64Alphabet((outputBitBuffer >> 12) & 0x0000003f);
            if (PadWithByte > 0) {
                outputBuffer[outputBufferSize++] = PadWithByte;
                outputBuffer[outputBufferSize++] = PadWithByte;
            }
            outputBitStep = 0; // Make sure to mark as complete

        } else if (outputBitStep == 2) {
            // Check if the output buffer needs to be flushed first
            if (PadWithByte > 0) {
                if (outputBufferSize > outputBuffer.Length - 4) {
                    wrapped.Write(outputBuffer, 0, outputBufferSize);
                    outputBufferSize = 0;
                }
            } else if (outputBufferSize > outputBuffer.Length - 3) {
                wrapped.Write(outputBuffer, 0, outputBufferSize);
                outputBufferSize = 0;
            }

            outputBuffer[outputBufferSize++] = ToBase64Alphabet((outputBitBuffer >> 18) & 0x0000003f);
            outputBuffer[outputBufferSize++] = ToBase64Alphabet((outputBitBuffer >> 12) & 0x0000003f);
            outputBuffer[outputBufferSize++] = ToBase64Alphabet((outputBitBuffer >> 6) & 0x0000003f);
            if (PadWithByte > 0) {
                outputBuffer[outputBufferSize++] = PadWithByte;
            }
            outputBitStep = 0; // Make sure to mark as complete
        }

        // Final flush check
        if (outputBufferSize > 0) {
            wrapped.Write(outputBuffer, 0, outputBufferSize);
            outputBufferSize = 0;
        }
        wrapped.Flush();
    }


    /// <summary>
    /// Helper method to convert the 6-bit byte to the base 64 alphabet.
    /// </summary>
    /// <param name="sixbit">The 6-bit byte</param>
    /// <returns>The base 64 letter.</returns>
    public byte ToBase64Alphabet(int sixbit) {
        switch (sixbit) {
            case 0b00000: return 65;  // 'A': ASCII code 65
            case 0b00001: return 66;
            case 0b00010: return 67;
            case 0b00011: return 68;
            case 0b00100: return 69;
            case 0b00101: return 70;
            case 0b00110: return 71;
            case 0b00111: return 72;
            case 0b01000: return 73;
            case 0b01001: return 74;
            case 0b01010: return 75;
            case 0b01011: return 76;
            case 0b01100: return 77;
            case 0b01101: return 78;
            case 0b01110: return 79;
            case 0b01111: return 80;
            case 0b10000: return 81;
            case 0b10001: return 82;
            case 0b10010: return 83;
            case 0b10011: return 84;
            case 0b10100: return 85;
            case 0b10101: return 86;
            case 0b10110: return 87;
            case 0b10111: return 88;
            case 0b11000: return 89;
            case 0b11001: return 90;

            // 'a': ASCII code 97
            case 0b11010: return 97;
            case 0b11011: return 98;
            case 0b11100: return 99;
            case 0b11101: return 100;
            case 0b11110: return 101;
            case 0b11111: return 102;
            case 0b100000: return 103;
            case 0b100001: return 104;
            case 0b100010: return 105;
            case 0b100011: return 106;
            case 0b100100: return 107;
            case 0b100101: return 108;
            case 0b100110: return 109;
            case 0b100111: return 110;
            case 0b101000: return 111;
            case 0b101001: return 112;
            case 0b101010: return 113;
            case 0b101011: return 114;
            case 0b101100: return 115;
            case 0b101101: return 116;
            case 0b101110: return 117;
            case 0b101111: return 118;
            case 0b110000: return 119;
            case 0b110001: return 120;
            case 0b110010: return 121;
            case 0b110011: return 122;

            // '0': ASCII code 48
            case 0b110100: return 48;
            case 0b110101: return 49;
            case 0b110110: return 50;
            case 0b110111: return 51;
            case 0b111000: return 52;
            case 0b111001: return 53;
            case 0b111010: return 54;
            case 0b111011: return 55;
            case 0b111100: return 56;
            case 0b111101: return 57;

            // '+': ASCII code 43
            case 0b111110: return Byte62;

            // '/': ASCII code 47
            case 0b111111: return Byte63;

            default: return (byte)'/';
        }
    }

    #endregion



    #region Reading

    /// <summary>
    /// Returns whether there is still data available for reading.  
    /// Substitute for <c>.Length</c>, which cannot be supported in this stream.
    /// </summary>
    /// <remarks>
    /// Depending on the underlying stream, this method may block for undetermined time.
    /// </remarks>
    /// <returns><c>true</c> if there is still data to be read, <c>false</c> otherwise.</returns>
    public bool HasData() {
        if (wrapped == null) {
            return false;
        } else if (inputBufferSize < 0 && inputBitStep >= inputBitSize) {
            return false;
        }

        if (inputBitStep < inputBitSize || inputBufferOffset < inputBufferSize) {
            return true;
        }

        // Check if we need to update the internal read buffer
        if (inputBufferOffset >= inputBufferSize) {
            inputBufferSize = wrapped.Read(inputBuffer, 0, inputBuffer.Length);
            if (inputBufferSize <= 0) {
                inputBufferSize = -1;
                return false;
            }
            inputBufferOffset = 0;
        }
        return true;
    }


    /// <summary>
    /// Returns whether this stream can be read from or not.
    /// </summary>
    public override bool CanRead {
        get {
            if (wrapped == null) {
                return false;
            }
            return wrapped.CanRead;
        }
    }


    /// <summary>
    /// Reads up to <c>CopyLength</c> decoded bytes from the underlying stream
    /// into the specified <c>CopyToBuffer</c>.  The starting point in
    /// <c>CopyToBuffer</c> is specified by <c>CopyFromOffset</c>.
    /// </summary>
    /// <param name="copyToBuffer">The byte[] array to copy the decoded data to.</param>
    /// <param name="copyFromOffset">The offset to start copying decoded data to.</param>
    /// <param name="copyLength">The maximum number of decoded bytes to copy in this call.</param>
    /// <returns>The next decoded data byte from the underlying stream.</returns>
    public override int Read(byte[] copyToBuffer, int copyFromOffset, int copyLength) {

        // Save original offset
        int copyOffset = copyFromOffset;
        int copyToEnd = copyOffset + copyLength;

        // Read any remaining bits from bit buffer
        while (inputBitStep < inputBitSize && copyOffset < copyToEnd) {
            int c = ReadByte();
            if (c < 0) {
                return copyOffset - copyFromOffset;
            }
            copyToBuffer[copyOffset++] = (byte)c;
        }

        // Check if this is the end
        if (inputBufferSize < 0) {
            return copyOffset - copyFromOffset;
        }

        // Remove nearest remainer of 3 bytes from end
        int leftoverBytes = (copyToEnd - copyOffset) % 3;
        copyToEnd -= leftoverBytes;

        while (copyOffset < copyToEnd) {

            // Reset buffers before first 6-bits
            inputBitBuffer = 0;

            // Read the first base64 letter
            int c = ReadB64Byte();

            // Put first 6 bit letter into buffer
            byte b = FromBase64Alphabet(c);
            inputBitBuffer = b << 18;

            // Read the second base64 letter
            c = ReadB64Byte();

            // Put second 6 bits into buffer
            b = FromBase64Alphabet(c);
            inputBitBuffer |= b << 12;

            // Read the third base64 letter
            c = ReadB64Byte();

            // Put third 6 bits into buffer
            b = FromBase64Alphabet(c);
            inputBitBuffer |= b << 6;

            // Read the last base64 letter
            c = ReadB64Byte();

            // Put final 6 bits into buffer
            b = FromBase64Alphabet(c);
            inputBitBuffer |= b;

            // Shift out the next bytes
            copyToBuffer[copyOffset++] = (byte)((inputBitBuffer >> 16) & 0x000000ff);
            copyToBuffer[copyOffset++] = (byte)((inputBitBuffer >> 8) & 0x000000ff);
            copyToBuffer[copyOffset++] = (byte)((inputBitBuffer) & 0x000000ff);

        }

        // Return to original end
        copyToEnd += leftoverBytes;

        // Copy leftover bits per byte
        while (copyOffset < copyToEnd) {
            int c = ReadByte();
            if (c < 0) {
                return copyOffset - copyFromOffset;
            }
            copyToBuffer[copyOffset++] = (byte)c;
        }

        return copyOffset - copyFromOffset;
    }


    /// <summary>
    /// Returns a single decoded byte from the base64 data 
    /// in the underlying stream.
    /// </summary>
    /// <returns>The next decoded byte from the Base64Stream, or -1 if the end of the stream has been reached.</returns>
    public int ReadByte() {
        if (inputBitStep < inputBitSize) {
            if (inputBitStep == 1) {
                // Return the second byte from the bit-buffer
                inputBitStep++;
                return (inputBitBuffer >> 8) & 0x000000ff;
            } else {
                // Return the final byte from the bit-buffer
                inputBitStep++;
                return inputBitBuffer & 0x000000ff;
            }
        } else {
            // Read the first base64 letter
            int c = ReadB64Byte();
            if (c < 0) {
                // No more data
                inputBitStep = 0;
                inputBitSize = 0;
                return -1;
            }

            // Reset buffers before first 6-bits
            inputBitBuffer = 0;

            // Put first 6 bit letter into buffer
            byte b = FromBase64Alphabet(c);
            // Read the second base64 letter
            c = ReadB64Byte();
            if (c < 0) {
                // Only had 6 bits left
                inputBitStep = 0;
                inputBitSize = 0; // We will return the last 6-bits now
                return (b << 2) & 0x000000ff;
            }

            inputBitBuffer = b << 18;
            inputBitStep = 1; // The first byte will be returned below


            // Put second 6 bits into buffer
            b = FromBase64Alphabet(c);
            inputBitBuffer |= b << 12;

            // Read the third base64 letter
            c = ReadB64Byte();
            if (c < 0) {
                // Only had 12 bits
                inputBitSize = 1; // There are 12 bits and will still be 4 bits remaining for one last byte
                return (inputBitBuffer >> 16) & 0x000000ff;
            }
            // Put third 6 bits into buffer
            b = FromBase64Alphabet(c);
            inputBitBuffer |= b << 6;

            // Read the fourth base64 letter
            c = ReadB64Byte();
            if (c < 0) {
                // Only had 18 bits remaining
                inputBitSize = 2; // There are 18 bits and will still be 10 bits left for 2 bytes
                return (inputBitBuffer >> 16) & 0x000000ff;
            }
            // Put fourth 6 bits into buffer
            b = FromBase64Alphabet(c);
            inputBitBuffer |= b;
            inputBitSize = 3; // There are 24 bits and will be 16 bits left for 2 more bytes

            // Return the next output byte
            return (inputBitBuffer >> 16) & 0x000000ff;
        }
    }


    /// <summary>
    /// Returns the next 6-bit value from the underlying stream.
    /// </summary>
    /// <returns>A byte with value [0-63] such that it can be recombined to it's full 8-bit value</returns>
    private int ReadB64Byte() {
        if (inputBufferSize < 0) {
            return -1;
        }

        // Check if we need to update the internal read buffer
        if (inputBufferOffset >= inputBufferSize) {
            inputBufferSize = wrapped.Read(inputBuffer, 0, inputBuffer.Length);
            if (inputBufferSize <= 0) {
                inputBufferSize = -1;
                return -1;
            }
            inputBufferOffset = 0;
        }


        byte b = inputBuffer[inputBufferOffset++];
        while (b <= ' ') { // Skip whitespace, including CRLFs

            // Check if we need to update the internal read buffer
            if (inputBufferOffset >= inputBufferSize) {
                inputBufferSize = wrapped.Read(inputBuffer, 0, inputBuffer.Length);
                if (inputBufferSize <= 0) {
                    inputBufferSize = -1;
                    return -1;
                }
                inputBufferOffset = 0;
            }

            b = inputBuffer[inputBufferOffset++];

        }
        if (b == '=') { // End of stream (at least, the base64 data)
            inputBufferSize = -1;
            return -1;
        } else if (PadWithByte > 0 && b == PadWithByte) { // End of stream (at least, the base64 data)
            inputBufferSize = -1;
            return -1;
        }
        return b;
    }


    /// <summary>
    /// Helper method to return the 6-bit byte from the base64 letter.
    /// </summary>
    /// <param name="letter">The 6-bit byte for the base 64 aphabet letter.</param>
    /// <returns></returns>
    private byte FromBase64Alphabet(int letter) {
        switch (letter) {

            // 'A': ASCII code 65
            case 65: return 0b00000000; // 0;
            case 66: return 0b00000001; // 1;
            case 67: return 0b00000010; // 2;
            case 68: return 0b00000011; // 3;
            case 69: return 0b00000100; // 4;
            case 70: return 0b00000101; // 5;
            case 71: return 0b00000110; // 6;
            case 72: return 0b00000111; // 7;
            case 73: return 0b00001000; // 8;
            case 74: return 0b00001001; // 9;
            case 75: return 0b00001010; // 10;
            case 76: return 0b00001011; // 11;
            case 77: return 0b00001100; // 12;
            case 78: return 0b00001101; // 13;
            case 79: return 0b00001110; // 14;
            case 80: return 0b00001111; // 15;
            case 81: return 0b00010000; // 16;
            case 82: return 0b00010001; // 17;
            case 83: return 0b00010010; // 18;
            case 84: return 0b00010011; // 19;
            case 85: return 0b00010100; // 20;
            case 86: return 0b00010101; // 21;
            case 87: return 0b00010110; // 22;
            case 88: return 0b00010111; // 23;
            case 89: return 0b00011000; // 24;
            case 90: return 0b00011001; // 25;

            // 'a': ASCII code 97
            case 97: return 0b00011010; // 26;
            case 98: return 0b00011011; // 27;
            case 99: return 0b00011100; // 28;
            case 100: return 0b00011101; // 29;
            case 101: return 0b00011110; // 30;
            case 102: return 0b00011111; // 31;
            case 103: return 0b00100000; // 32;
            case 104: return 0b00100001; // 33;
            case 105: return 0b00100010; // 34;
            case 106: return 0b00100011; // 35;
            case 107: return 0b00100100; // 36;
            case 108: return 0b00100101; // 37;
            case 109: return 0b00100110; // 38;
            case 110: return 0b00100111; // 39;
            case 111: return 0b00101000; // 40;
            case 112: return 0b00101001; // 41;
            case 113: return 0b00101010; // 42;
            case 114: return 0b00101011; // 43;
            case 115: return 0b00101100; // 44;
            case 116: return 0b00101101; // 45;
            case 117: return 0b00101110; // 46;
            case 118: return 0b00101111; // 47;
            case 119: return 0b00110000; // 48;
            case 120: return 0b00110001; // 49;
            case 121: return 0b00110010; // 50;
            case 122: return 0b00110011; // 51;

            // '0': ASCII code 
            case 48: return 0b00110100; // 52;
            case 49: return 0b00110101; // 53;
            case 50: return 0b00110110; // 54;
            case 51: return 0b00110111; // 55;
            case 52: return 0b00111000; // 56;
            case 53: return 0b00111001; // 57;
            case 54: return 0b00111010; // 58;
            case 55: return 0b00111011; // 59;
            case 56: return 0b00111100; // 60;
            case 57: return 0b00111101; // 61;

            default:
                if (letter == Byte62) {
                    return 0b00111110; // 62;
                } else if (letter == Byte63) {
                    return 0b00111111; // 63;
                }
                return 0;
        }
    }
    #endregion

}
