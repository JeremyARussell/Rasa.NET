﻿using System;
using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace Rasa.Packets.Protocol
{
    using Data;
    using Memory;

    public class ProtocolPacket : IBasePacket
    {
        public ClientMessageOpcode Type { get; private set; } = ClientMessageOpcode.None;

        public ushort Size { get; private set; }
        public byte Channel { get; private set; }
        public uint SequenceNumber { get; set; }
        public bool Compress { get; private set; }
        public IClientMessage Message { get; set; }

        public ProtocolPacket()
        {
        }

        public ProtocolPacket(IClientMessage message, ClientMessageOpcode type, bool compress, byte channel)
        {
            Message = message;
            Type = type;
            Compress = compress;
            Channel = channel;
        }

        public void Read(BinaryReader br)
        {
            if (br.BaseStream.Length < 4)
                throw new Exception("Fragmented receive, should not happen! (4 size header)");

            Size = br.ReadUInt16();
            Channel = br.ReadByte();

            br.ReadByte(); // padding

            if (Size > br.BaseStream.Length)
            {
                Debugger.Break();

                throw new Exception($"Fragmented receive, should not happen! Packet size: {Size} <-> Buffer length: {br.BaseStream.Length}");
            }

            if (Channel == 0xFF) // Internal channel: Send timeout checking, ignore the packet
                return;

            if (Channel != 0) // 0 == ReliableStreamChannel (no extra data), Move message uses channels
            {
                SequenceNumber = br.ReadUInt32(); // Sequence number? if (previousValue - newValue < 0) { process packet; previousValue = newValue; }
                br.ReadInt32(); // 0xDEADBEEF
                br.ReadInt32(); // skip
            }

            var packetBeginPosition = br.BaseStream.Position;

            using (var reader = new ProtocolBufferReader(br, ProtocolBufferFlags.DontFragment))
            {
                reader.ReadProtocolFlags();
                reader.ReadPacketType(out ushort type, out bool compress);

                Type = (ClientMessageOpcode) type;
                Compress = compress;

                reader.ReadXORCheck((int) (br.BaseStream.Position - packetBeginPosition));
            }

            var xorCheckPosition = (int) br.BaseStream.Position;

            var readBr = br;

            byte[] uncompressedBuffer = null;

            if (Compress)
            {
                var someType = br.ReadByte(); // 0 = No compression
                if (someType >= 2)
                    throw new Exception("Invalid compress type received!");

                if (someType == 1)
                {
                    Debugger.Break(); // TODO: test

                    var uncompressedSize = br.ReadInt32();

                    uncompressedBuffer = ArrayPool<byte>.Shared.Rent(uncompressedSize);

                    using (var deflateStream = new DeflateStream(br.BaseStream, CompressionMode.Decompress, true)) // TODO: test if the br.BaseStream is cool as the Stream input for the DeflateStream
                        deflateStream.Read(uncompressedBuffer, 0, uncompressedSize);

                    readBr = new BinaryReader(new MemoryStream(uncompressedBuffer, 0, uncompressedSize, false), Encoding.UTF8, false);
                }
            }

            Message = Type switch
            {
                ClientMessageOpcode.Login => new LoginMessage(),
                ClientMessageOpcode.Move => new MoveMessage(),
                ClientMessageOpcode.CallServerMethod => new CallServerMethodMessage(),
                ClientMessageOpcode.Ping => new PingMessage(),
                _ => throw new Exception($"Unable to handle packet type {Type}, because it's a Server -> Client packet!"),
            };

            using (var reader = new ProtocolBufferReader(readBr, ProtocolBufferFlags.DontFragment))
            {
                reader.ReadProtocolFlags();

                // Subtype and Message.Read()
                reader.ReadDebugByte(41);

                if ((Message.SubtypeFlags & ClientMessageSubtypeFlag.HasSubtype) == ClientMessageSubtypeFlag.HasSubtype)
                {
                    Message.RawSubtype = reader.ReadByte();
                    if (Message.RawSubtype < Message.MinSubtype || Message.RawSubtype > Message.MaxSubtype)
                        throw new Exception("Invalid Subtype found!");
                }

                Message.Read(reader);

                reader.ReadDebugByte(42);

                reader.ReadXORCheck((int) br.BaseStream.Position - xorCheckPosition);
            }

            // If we rented a buffer for decompressing, return it
            if (uncompressedBuffer != null) 
                ArrayPool<byte>.Shared.Return(uncompressedBuffer);
        }

        public void Write(BinaryWriter bw)
        {
            var sizePosition = bw.BaseStream.Position;

            bw.Write((ushort) 0); // Size placeholder

            bw.Write(Channel);
            bw.Write((byte) 0); // padding

            if (Channel != 0)
            {
                bw.Write(SequenceNumber); // sequence num?
                bw.Write(0xDEADBEEF); // const
                bw.Write(0); // padding
            }

            var packetBeginPosition = (int) bw.BaseStream.Position;

            // TODO: find limits and maybe lower this number
            // OR: use NCMS as the target stream
            var packetBuffer = ArrayPool<byte>.Shared.Rent(0x8000);
            int uncompressedSize;

            using (var ms = new MemoryStream(packetBuffer, true))
            {
                using var packetWriter = new BinaryWriter(ms, Encoding.UTF8, true);
                using var writer = new ProtocolBufferWriter(packetWriter, ProtocolBufferFlags.DontFragment);

                writer.WriteProtocolFlags();

                writer.WriteDebugByte(41);

                if ((Message.SubtypeFlags & ClientMessageSubtypeFlag.HasSubtype) == ClientMessageSubtypeFlag.HasSubtype)
                    writer.WriteByte(Message.RawSubtype);

                Message.Write(writer);

                writer.WriteDebugByte(42);

                var currentPos = (int)ms.Position;

                writer.WriteXORCheck(currentPos);

                uncompressedSize = (int)ms.Position;
            }

            var compress = (Message.SubtypeFlags & ClientMessageSubtypeFlag.Compress) == ClientMessageSubtypeFlag.Compress && uncompressedSize > 0;

            using (var writer = new ProtocolBufferWriter(bw, ProtocolBufferFlags.DontFragment))
            {
                writer.WriteProtocolFlags();

                writer.WritePacketType((ushort) Message.Type, compress);

                writer.WriteXORCheck((int) (bw.BaseStream.Position - packetBeginPosition));

            }

            int packetSize = uncompressedSize;

            if (compress) // TODO: test
            {
                bw.Write((byte) 0x01);
                bw.Write(uncompressedSize);

                var compressedBuffer = ArrayPool<byte>.Shared.Rent(uncompressedSize);
                int compressedSize;

                using (var compressStream = new MemoryStream(compressedBuffer, true))
                {
                    using (var compressorStream = new DeflateStream(compressStream, CompressionMode.Compress, true))
                        compressorStream.Write(packetBuffer, 0, uncompressedSize);

                    compressedSize = (int) compressStream.Position;
                }

                ArrayPool<byte>.Shared.Return(packetBuffer);

                packetBuffer = compressedBuffer;
                packetSize = compressedSize;
            }

            bw.Write(packetBuffer, 0, packetSize);

            ArrayPool<byte>.Shared.Return(packetBuffer);

            var currentPosition = bw.BaseStream.Position;

            bw.BaseStream.Position = sizePosition;

            bw.Write((ushort) (currentPosition - sizePosition));

            bw.BaseStream.Position = currentPosition;
        }
    }
}
