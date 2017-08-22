using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Collections.Generic;
using Commons;
using System.Drawing;
using ATL.Logging;
using System.Drawing.Imaging;

namespace ATL.AudioData.IO
{
    /// <summary>
    /// Class for ID3v2.2-2.4 tags manipulation
    /// 
    /// Implementation notes
    /// 
    ///     1. Extended header tags
    /// 
    ///     Due to the rarity of ID3v2 tags with extended headers (on my disk and on the web), 
    ///     implementation of decoding extended header data is still theoretical
    /// 
    /// </summary>
    public class ID3v2 : MetaDataIO
    {
        public const byte TAG_VERSION_2_2 = 2;             // Code for ID3v2.2.x tag
        public const byte TAG_VERSION_2_3 = 3;             // Code for ID3v2.3.x tag
        public const byte TAG_VERSION_2_4 = 4;             // Code for ID3v2.4.x tag

        private String FEncoder;
        private String FLanguage;
        private String FLink;

        private TagInfo FTagHeader;

        public String Encoder // Encoder
        {
            get { return this.FEncoder; }
            set { FEncoder = value; }
        }
        public String Language // Language
        {
            get { return this.FLanguage; }
            set { FLanguage = value; }
        }
        public String Link // URL link
        {
            get { return this.FLink; }
            set { FLink = value; }
        }

        // ID3v2 tag ID
        private const String ID3V2_ID = "ID3";

        // List of standard fields
        private static ICollection<string> standardFrames_v22;
        private static ICollection<string> standardFrames_v23;
        private static ICollection<string> standardFrames_v24;

        // Mapping between ID3v2 field IDs and ATL fields
        private static IDictionary<string, byte> frameMapping_v22;
        private static IDictionary<string, byte> frameMapping_v23_24;

        // Max. tag size for saving
        private const int ID3V2_MAX_SIZE = 4096;

        // Frame header (universal)
        private class FrameHeader
        {
            public string ID;                           // Frame ID
            public int Size;                            // Size excluding header
            public ushort Flags;				        // Flags
        }

        // ID3v2 header data - for internal use
        private class TagInfo
        {
            // Real structure of ID3v2 header
            public char[] ID = new char[3];                            // Always "ID3"
            public byte Version;                                     // Version number
            public byte Revision;                                   // Revision number
            public byte Flags;                                         // Flags of tag
            public byte[] Size = new byte[4];             // Tag size excluding header
            // Extended data
            public long FileSize;                                 // File size (bytes)

            // Extended header flags
            public int ExtendedHeaderSize = 0;
            public int ExtendedFlags;
            public int CRC = -1;
            public int TagRestrictions = -1;


            // **** BASE HEADER PROPERTIES ****
            public bool UsesUnsynchronisation
            {
                get { return ((Flags & 128) > 0); }
            }
            public bool HasExtendedHeader // Determinated from flags; indicates if tag has an extended header (ID3v2.3+)
            {
                get { return (((Flags & 64) > 0) && (Version > TAG_VERSION_2_2)); }
            }
            public bool IsExperimental // Determinated from flags; indicates if tag is experimental (ID3v2.4+)
            {
                get { return ((Flags & 32) > 0); }
            }
            public bool HasFooter // Determinated from flags; indicates if tag has a footer (ID3v2.4+)
            {
                get { return ((Flags & 0x10) > 0); }
            }

            // **** EXTENDED HEADER PROPERTIES ****
            public int TagFramesRestriction
            {
                get
                {
                    switch ((TagRestrictions & 0xC0) >> 6)
                    {
                        case 0: return 128;
                        case 1: return 64;
                        case 2: return 32;
                        case 3: return 32;
                        default: return -1;
                    }
                }
            }
            public int TagSizeRestrictionKB
            {
                get {
                    switch ((TagRestrictions & 0xC0) >> 6)
                    {
                        case 0: return 1024;
                        case 1: return 128;
                        case 2: return 40;
                        case 3: return 4;
                        default: return -1;
                    }
                }
            }
            public bool HasTextEncodingRestriction
            {
                get { return (((TagRestrictions & 0x20) >> 5) > 0); }
            }
            public int TextFieldSizeRestriction
            {
                get
                {
                    switch ((TagRestrictions & 0x18) >> 3)
                    {
                        case 0: return -1;
                        case 1: return 1024;
                        case 2: return 128;
                        case 3: return 30;
                        default: return -1;
                    }
                }
            }
            public bool HasPictureEncodingRestriction
            {
                get { return (((TagRestrictions & 0x04) >> 2) > 0); }
            }
            public int PictureSizeRestriction
            {
                get
                {
                    switch ((TagRestrictions & 0x03))
                    {
                        case 0: return -1;  // No restriction
                        case 1: return 256; // 256x256 or less
                        case 2: return 63;  // 64x64 or less
                        case 3: return 64;  // Exactly 64x64
                        default: return -1;
                    }
                }
            }
        }

        // Unicode BOM properties
        private class BOMProperties
        {
            public int Size = 0;                // Size of BOM
            public Encoding Encoding;           // Corresponding encoding
        }

        // ********************* Auxiliary functions & voids ********************

        static ID3v2()
        {
            standardFrames_v22 = new List<string>() { "BUF", "CNT", "COM", "CRA", "CRM", "ETC", "EQU", "GEO", "IPL", "LNK", "MCI", "MLL", "PIC", "POP", "REV", "RVA", "SLT", "STC", "TAL", "TBP", "TCM", "TCO", "TCR", "TDA", "TDY", "TEN", "TFT", "TIM", "TKE", "TLA", "TLE", "TMT", "TOA", "TOF", "TOL", "TOR", "TOT", "TP1", "TP2", "TP3", "TP4", "TPA", "TPB", "TRC", "TRD", "TRK", "TSI", "TSS", "TT1", "TT2", "TT3", "TXT", "TXX", "TYE","UFI","ULT","WAF","WAR","WAS","WCM","WCP","WPB","WXX" };
            standardFrames_v23 = new List<string>() { "AENC","APIC","COMM","COMR","ENCR","EQUA","ETCO","GEOB","GRID","IPLS","LINK","MCDI","MLLT","OWNE","PRIV","PCNT","POPM","POSS","RBUF","RVAD","RVRB","SYLT","SYTC","TALB","TBPM","TCOM","TCON","TCOP","TDAT","TDLY","TENC","TEXT","TFLT","TIME","TIT1", "TIT2", "TIT3","TKEY","TLAN","TLEN","TMED","TOAL","TOFN","TOLY","TOPE","TORY","TOWN","TPE1", "TPE2", "TPE3", "TPE4","TPOS","TPUB","TRCK","TRDA","TRSN","TRSO","TSIZ","TSRC","TSSE","TYER","TXXX","UFID","USER","USLT","WCOM","WCOP","WOAF","WOAR","WOAS","WORS","WPAY","WPUB","WXXX" };
            standardFrames_v24 = new List<string>() { "AENC", "APIC", "ASPI","COMM", "COMR", "ENCR", "EQU2", "ETCO", "GEOB", "GRID", "LINK", "MCDI", "MLLT", "OWNE", "PRIV", "PCNT", "POPM", "POSS", "RBUF", "RVA2", "RVRB", "SEEK","SIGN","SYLT", "SYTC", "TALB", "TBPM", "TCOM", "TCON", "TCOP", "TDEN", "TDLY", "TDOR","TDRC","TDRL","TDTG", "TENC", "TEXT", "TFLT", "TIPL", "TIT1", "TIT2", "TIT3", "TKEY", "TLAN", "TLEN", "TMCL","TMED", "TMOO","TOAL", "TOFN", "TOLY", "TOPE", "TORY", "TOWN", "TPE1", "TPE2", "TPE3", "TPE4", "TPOS", "TPRO", "TPUB", "TRCK", "TRSN", "TRSO", "TSOA","TSOP","TSOT", "TSRC", "TSSE", "TSST","TXXX", "UFID", "USER", "USLT", "WCOM", "WCOP", "WOAF", "WOAR", "WOAS", "WORS", "WPAY", "WPUB", "WXXX" };

            // Note on date field identifiers
            //
            // Original release date
            //   ID3v2.0 : TOR (year only)
            //   ID3v2.3 : TORY (year only)
            //   ID3v2.4 : TDOR (timestamp according to spec; actual content may vary)
            //
            // Release date
            //   ID3v2.0 : no standard
            //   ID3v2.3 : no standard
            //   ID3v2.4 : TDRL (timestamp according to spec; actual content may vary)
            //
            // Recording date <== de facto standard behind the "date" field on most taggers
            //   ID3v2.0 : TYE (year), TDA (day & month - DDMM)
            //   ID3v2.3 : TYER (year), TDAT (day & month - DDMM)
            //   ID3v2.4 : TDRC (timestamp according to spec; actual content may vary)

            // Mapping between standard ATL fields and ID3v2.2 identifiers
            frameMapping_v22 = new Dictionary<string, byte>
            {
                { "TT1", TagData.TAG_FIELD_GENERAL_DESCRIPTION },
                { "TT2", TagData.TAG_FIELD_TITLE },
                { "TP1", TagData.TAG_FIELD_ARTIST },
                { "TP2", TagData.TAG_FIELD_ALBUM_ARTIST },  // De facto standard, regardless of spec
                { "TP3", TagData.TAG_FIELD_CONDUCTOR },
                { "TOA", TagData.TAG_FIELD_ORIGINAL_ARTIST },
                { "TAL", TagData.TAG_FIELD_ALBUM },
                { "TOT", TagData.TAG_FIELD_ORIGINAL_ALBUM },
                { "TRK", TagData.TAG_FIELD_TRACK_NUMBER },
                { "TPA", TagData.TAG_FIELD_DISC_NUMBER },
                { "TYE", TagData.TAG_FIELD_RECORDING_YEAR },
                { "TDA", TagData.TAG_FIELD_RECORDING_DAYMONTH },
                { "COM", TagData.TAG_FIELD_COMMENT },
                { "TCM", TagData.TAG_FIELD_COMPOSER },
                { "POP", TagData.TAG_FIELD_RATING },
                { "TCO", TagData.TAG_FIELD_GENRE },
                { "TCR", TagData.TAG_FIELD_COPYRIGHT },
                { "TPB", TagData.TAG_FIELD_PUBLISHER }
            };

            // Mapping between standard fields and ID3v2.3+ identifiers
            frameMapping_v23_24 = new Dictionary<string, byte>
            {
                { "TIT1", TagData.TAG_FIELD_GENERAL_DESCRIPTION },
                { "TIT2", TagData.TAG_FIELD_TITLE },
                { "TPE1", TagData.TAG_FIELD_ARTIST },
                { "TPE2", TagData.TAG_FIELD_ALBUM_ARTIST }, // De facto standard, regardless of spec
                { "TPE3", TagData.TAG_FIELD_CONDUCTOR },
                { "TOPE", TagData.TAG_FIELD_ORIGINAL_ARTIST },
                { "TALB", TagData.TAG_FIELD_ALBUM },
                { "TOAL", TagData.TAG_FIELD_ORIGINAL_ALBUM },
                { "TRCK", TagData.TAG_FIELD_TRACK_NUMBER },
                { "TPOS", TagData.TAG_FIELD_DISC_NUMBER },
                { "TDRC", TagData.TAG_FIELD_RECORDING_DATE },
                { "TYER", TagData.TAG_FIELD_RECORDING_YEAR },
                { "TDAT", TagData.TAG_FIELD_RECORDING_DAYMONTH },
                { "COMM", TagData.TAG_FIELD_COMMENT },
                { "TCOM", TagData.TAG_FIELD_COMPOSER },
                { "POPM", TagData.TAG_FIELD_RATING },
                { "TCON", TagData.TAG_FIELD_GENRE },
                { "TCOP", TagData.TAG_FIELD_COPYRIGHT },
                { "TPUB", TagData.TAG_FIELD_PUBLISHER }
            };
        }

        public ID3v2()
        {
            ResetData();
        }

        private bool readHeader(BinaryReader SourceFile, ref TagInfo Tag, long offset)
        {
            bool result = true;

            // Reads mandatory (base) header
            SourceFile.BaseStream.Seek(offset, SeekOrigin.Begin);
            Tag.ID = StreamUtils.ReadOneByteChars(SourceFile, 3);

            if (!StreamUtils.StringEqualsArr(ID3V2_ID, FTagHeader.ID)) return false;

            Tag.Version = SourceFile.ReadByte();
            Tag.Revision = SourceFile.ReadByte();
            Tag.Flags = SourceFile.ReadByte();
            
            // ID3v2 tag size
            Tag.Size = SourceFile.ReadBytes(4);

            // Reads optional (extended) header
            if (Tag.HasExtendedHeader)
            {
                Tag.ExtendedHeaderSize = StreamUtils.DecodeSynchSafeInt(SourceFile.ReadBytes(4)); // Extended header size
                SourceFile.BaseStream.Seek(1, SeekOrigin.Current); // Number of flag bytes; always 1 according to spec

                Tag.ExtendedFlags = SourceFile.BaseStream.ReadByte();

                if ((Tag.ExtendedFlags & 64) > 0) // Tag is an update
                {
                    // This flag is informative and has no corresponding data
                }
                if ( (Tag.ExtendedFlags & 32) > 0) // CRC present
                {
                    Tag.CRC = StreamUtils.DecodeSynchSafeInt(SourceFile.ReadBytes(5));
                }
                if ((Tag.ExtendedFlags & 16) > 0) // Tag has at least one restriction
                {
                    Tag.TagRestrictions = SourceFile.BaseStream.ReadByte();
                }
            }

            // File size
            Tag.FileSize = SourceFile.BaseStream.Length;

            return result;
        }

        // ---------------------------------------------------------------------------

        private int getTagSize(TagInfo Tag)
        {
            // Get total tag size
            int result = StreamUtils.DecodeSynchSafeInt32(Tag.Size) + 10;

            if (Tag.HasFooter) result += 10; // Indicates the presence of a footer (ID3v2.4+)
            if (result > Tag.FileSize) result = 0;

            return result;
        }

        // ---------------------------------------------------------------------------

        private void setMetaField(String ID, String Data, ref TagInfo Tag, bool readAllMetaFrames)
        {
            byte supportedMetaId = 255;
            ID = ID.ToUpper();

            // Finds the ATL field identifier according to the ID3v2 version
            if (Tag.Version > TAG_VERSION_2_2)
            {
                if (frameMapping_v23_24.ContainsKey(ID)) supportedMetaId = frameMapping_v23_24[ID];
            } else
            {
                if (frameMapping_v22.ContainsKey(ID)) supportedMetaId = frameMapping_v22[ID];
            }

            TagData.MetaFieldInfo fieldInfo;
            // If ID has been mapped with an ATL field, store it in the dedicated place...
            if (supportedMetaId < 255)
            {
                if (TagData.TAG_FIELD_GENRE == supportedMetaId)
                {
                    tagData.IntegrateValue(supportedMetaId, extractGenreFromID3v2Code(Data));
                }
                else
                {
                    tagData.IntegrateValue(supportedMetaId, Data);
                }
            }
            else if (readAllMetaFrames) // ...else store it in the additional fields Dictionary
            {
                fieldInfo = new TagData.MetaFieldInfo(getImplementedTagType(), ID, Data);
                if (tagData.AdditionalFields.Contains(fieldInfo)) // Replace current value, since there can be no duplicate fields in ID3v2
                {
                    tagData.AdditionalFields.Remove(fieldInfo);
                }
                tagData.AdditionalFields.Add(fieldInfo);
            }
        }

        // Get information from frames (universal)
        private void readFrames(BinaryReader source, ref TagInfo tag, long offset, ReadTagParams readTagParams)
        {
            Stream fs = source.BaseStream;
            FrameHeader Frame = new FrameHeader();
            int encodingCode;
            long dataSize;
            long dataPosition;
            string strData;
            Encoding frameEncoding;

            fs.Seek(offset + 10, SeekOrigin.Begin);

            while ((fs.Position - offset < getTagSize(tag)) && (fs.Position < fs.Length))
            {
                // Read frame header and check frame ID
                Frame.ID = (TAG_VERSION_2_2 == tagVersion) ? Utils.Latin1Encoding.GetString(source.ReadBytes(3)) : Utils.Latin1Encoding.GetString(source.ReadBytes(4));

                if (!(Char.IsLetter(Frame.ID[0]) && Char.IsUpper(Frame.ID[0])))
                {
                    LogDelegator.GetLogDelegate()(Log.LV_ERROR, "Valid frame not found where expected; parsing interrupted"); // TODO - there are still too many of these
                    break;
                }

                // Frame size measures number of bytes between end of flag and end of payload
                /* Frame size encoding conventions
                    ID3v2.2 : 3 byte
                    ID3v2.3 : 4 byte
                    ID3v2.4 : synch-safe Int32
                */
                if (TAG_VERSION_2_2 == tagVersion)
                {
                    byte[] size = source.ReadBytes(3);
                    Frame.Size = (size[0] << 16) + (size[1] << 8) + size[2];
                }
                else if (TAG_VERSION_2_3 == tagVersion) Frame.Size = StreamUtils.ReverseInt32(source.ReadInt32());
                else if (TAG_VERSION_2_4 == tagVersion)
                {
                    byte[] size = source.ReadBytes(4);
                    Frame.Size = StreamUtils.DecodeSynchSafeInt32(size);
                }

                if (TAG_VERSION_2_2 == tagVersion)
                {
                    Frame.Flags = 0;
                }
                else
                {
                    Frame.Flags = StreamUtils.ReverseUInt16(source.ReadUInt16());
                }
                dataSize = Frame.Size - 1; // Minus encoding byte

                // Skips data size indicator if signaled by the flag
                if ((Frame.Flags & 1) > 0)
                {
                    fs.Seek(4, SeekOrigin.Current);
                    dataSize = dataSize - 4;
                }

                encodingCode = fs.ReadByte();
                frameEncoding = decodeID3v2CharEncoding((byte)encodingCode);

                // COMM fields contain :
                //   a 3-byte langage ID
                //   a "short content description", as an encoded null-terminated string
                //   the actual comment, as an encoded, null-terminated string
                // => lg lg lg (BOM) (encoded description) 00 (00) (BOM) encoded text 00 (00)
                if ("COM".Equals(Frame.ID.Substring(0, 3)))
                {
                    long initialPos = fs.Position;

                    // Skip langage ID
                    fs.Seek(3, SeekOrigin.Current);

                    BOMProperties contentDescriptionBOM = new BOMProperties();
                    // Skip BOM if ID3v2.3+ and UTF-16 with BOM present
                    if ( tagVersion > TAG_VERSION_2_2 && (1 == encodingCode) )
                    {
                        contentDescriptionBOM = readBOM(ref fs);
                    }

                    if (contentDescriptionBOM.Size <= 3)
                    {
                        // Skip content description
                        StreamUtils.ReadNullTerminatedString(source, frameEncoding);
                    }
                    else
                    {
                        // If content description BOM > 3 bytes, there might not be any BOM
                        // for content description, and the algorithm might have bumped into
                        // the comment BOM => backtrack just after langage tag
                        fs.Seek(initialPos + 3, SeekOrigin.Begin);
                    }

                    dataSize = dataSize - (fs.Position - initialPos);
                }

                // A $01 "Unicode" encoding flag means the presence of a BOM (Byte Order Mark) if version > 2.2
                // http://en.wikipedia.org/wiki/Byte_order_mark
                //    3-byte BOM : FF 00 FE
                //    2-byte BOM : FE FF (UTF-16 Big Endian)
                //    2-byte BOM : FF FE (UTF-16 Little Endian)
                //    Other variants...
                if (tagVersion > TAG_VERSION_2_2 && (1 == encodingCode))
                {
                    long initialPos = fs.Position;
                    BOMProperties bom = readBOM(ref fs);

                    // A BOM has been read, but it lies outside the current frame
                    // => Backtrack and directly read data without BOM
                    if (bom.Size > dataSize)
                    {
                        fs.Seek(initialPos, SeekOrigin.Begin);
                    }
                    else
                    {
                        frameEncoding = bom.Encoding;
                        dataSize = dataSize - bom.Size;
                    }
                }
                
                // If encoding > 3, we might have caught an actual character, which means there is no encoding flag
                if (encodingCode > 3)
                {
                    frameEncoding = decodeID3v2CharEncoding(0);
                    fs.Seek(-1, SeekOrigin.Current);
                    dataSize++;
                }


                // == READ ACTUAL FRAME DATA

                dataPosition = fs.Position;
                if ((dataSize > 0) && (dataSize < 500))
                {
                    // Specific to Popularitymeter : Rating data has to be extracted from the POPM block
                    if ("POP".Equals(Frame.ID.Substring(0,3)))
                    {
                        /*
                         * ID3v2.0 : According to spec (see �3.2), encoding should actually be ISO-8859-1
                         * ID3v2.3+ : Spec is unclear wether to read as ISO-8859-1 or not. Practice indicates using this convention is safer.
                         */
                        strData = readRatingInPopularityMeter(source, Utils.Latin1Encoding).ToString();
                    }
                    else if ("TXX".Equals(Frame.ID.Substring(0,3)))
                    {
                        // Read frame data and set tag item if frame supported
                        byte[] bData = source.ReadBytes((int)dataSize);
                        strData = Utils.StripEndingZeroChars(frameEncoding.GetString(bData));

                        string[] tabS = strData.Split('\0');
                        Frame.ID = tabS[0];
                        strData = tabS[1];
                    }
                    else
                    {
                        // Read frame data and set tag item if frame supported
                        byte[] bData = source.ReadBytes((int)dataSize);
                        strData = Utils.StripEndingZeroChars(frameEncoding.GetString(bData));
                    }

                    setMetaField(Frame.ID, strData, ref tag, readTagParams.ReadAllMetaFrames);

                    if (TAG_VERSION_2_2 == tagVersion) fs.Seek(dataPosition + dataSize, SeekOrigin.Begin);
                }
                else if (dataSize > 0) // Size > 500 => Probably an embedded picture
                {
                    long position = fs.Position;
                    if ("PIC".Equals(Frame.ID) || "APIC".Equals(Frame.ID))
                    {
                        ImageFormat imgFormat;
                        if (TAG_VERSION_2_2 == tagVersion)
                        {
                            // Image format
                            String imageFormat = new String(StreamUtils.ReadOneByteChars(source, 3)).ToUpper();

                            if ("BMP".Equals(imageFormat)) imgFormat = ImageFormat.Bmp;
                            else if ("PNG".Equals(imageFormat)) imgFormat = ImageFormat.Png;
                            else if ("GIF".Equals(imageFormat)) imgFormat = ImageFormat.Gif;
                            else imgFormat = ImageFormat.Jpeg;
                        }
                        else
                        {
                            // mime-type always coded in ASCII
                            if (1 == encodingCode) fs.Seek(-1, SeekOrigin.Current);
                            // Mime-type
                            String mimeType = StreamUtils.ReadNullTerminatedString(source, Utils.Latin1Encoding);
                            imgFormat = Utils.GetImageFormatFromMimeType(mimeType);
                        }

                        byte picCode = source.ReadByte();
                        // TODO factorize : abstract PictureTypeDecoder + unsupported / supported decision in MetaDataIO ? 
                        TagData.PIC_TYPE picType = DecodeID3v2PictureType(picCode);

                        int picturePosition;
                        if (picType.Equals(TagData.PIC_TYPE.Unsupported))
                        {
                            addPictureToken(MetaDataIOFactory.TAG_ID3V2,picCode);
                            picturePosition = takePicturePosition(MetaDataIOFactory.TAG_ID3V2, picCode);
                        }
                        else
                        {
                            addPictureToken(picType);
                            picturePosition = takePicturePosition(picType);
                        }

                        // Image description (unused)
                        // Description can be coded with another convention
                        if (tagVersion > TAG_VERSION_2_2 && (1 == encodingCode)) readBOM(ref fs);
                        StreamUtils.ReadNullTerminatedString(source, frameEncoding);

                        if (readTagParams.PictureStreamHandler != null)
                        {
                            int picSize = (int)(dataSize - (fs.Position - position));
                            MemoryStream mem = new MemoryStream(picSize);

                            if (tag.UsesUnsynchronisation)
                            {
                                decodeUnsynchronizedStreamTo(mem, source, picSize);
                            }
                            else
                            {
                                StreamUtils.CopyStream(source.BaseStream, mem, picSize);
                            }

                            mem.Seek(0, SeekOrigin.Begin);

                            readTagParams.PictureStreamHandler(ref mem, picType, imgFormat, MetaDataIOFactory.TAG_ID3V2, picCode, picturePosition);

                            mem.Close();
                        }
                    }
                    fs.Seek(position + dataSize, SeekOrigin.Begin);
                } // End picture frame
            } // End frames loop
        }

        protected override void resetSpecificData()
        {
            // No specific behaviour here
        }

        // ********************** Public functions & voids **********************

        public override bool Read(BinaryReader source, ReadTagParams readTagParams)
        {
            return Read(source, 0, readTagParams);
        }

        /// <summary>
        /// Reads ID3v2 data
        /// </summary>
        /// <param name="source">Reader object from where to read ID3v2 data</param>
        /// <param name="pictureStreamHandler">If not null, handler that will be triggered whenever a supported embedded picture is read</param>
        /// <param name="offset">ID3v2 header offset (mostly 0, except for specific audio containers such as AIFF or DSF)</param>
        /// <param name="storeUnsupportedMetaFields">Indicates wether unsupported fields should be read and stored in memory (optional; default = false)</param>
        /// <returns></returns>
        public bool Read(BinaryReader source, long offset, ReadTagParams readTagParams)
        {
            FTagHeader = new TagInfo();

            // Reset data and load header from file to variable
            ResetData();
            bool result = readHeader(source, ref FTagHeader, offset);

            // Process data if loaded and header valid
            if ((result) && StreamUtils.StringEqualsArr(ID3V2_ID, FTagHeader.ID))
            {
                tagExists = true;
                structureHelper.AddZone(offset, getTagSize(FTagHeader));
                // Fill properties with header data
                tagVersion = FTagHeader.Version;

                // Get information from frames if version supported
                if ((TAG_VERSION_2_2 <= tagVersion) && (tagVersion <= TAG_VERSION_2_4) && (Size > 0))
                {
                    tagData = new TagData();
                    readFrames(source, ref FTagHeader, offset, readTagParams);
                }
                else
                {
                    if ( (tagVersion < TAG_VERSION_2_2) ||  (tagVersion > TAG_VERSION_2_4) ) LogDelegator.GetLogDelegate()(Log.LV_ERROR, "ID3v2 tag version unknown : " + tagVersion  + "; parsing interrupted");
                    if (0 == Size) LogDelegator.GetLogDelegate()(Log.LV_ERROR, "ID3v2 size is zero; parsing interrupted");
                }
            }

            return result;
        }

        protected override int getDefaultTagOffset()
        {
            return TO_BOF;
        }

        protected override int getImplementedTagType()
        {
            return MetaDataIOFactory.TAG_ID3V2;
        }


        // Writes tag info using ID3v2.4 conventions
        // TODO much later : support ID3v2.3- conventions

        /// <summary>
        /// Writes the given tag into the given Writer using ID3v2.4 conventions
        /// </summary>
        /// <param name="tag">Tag information to be written</param>
        /// <param name="w">Stream to write tag information to</param>
        /// <returns>True if writing operation succeeded; false if not</returns>
        protected override bool write(TagData tag, BinaryWriter w, string zone)
        {
            bool result;
            long tagSizePos;
            int tagSize;

            w.Write(ID3V2_ID.ToCharArray());

            // Version 2.4.0
            w.Write(TAG_VERSION_2_4);
            w.Write((byte)0);

            // Flags : keep initial flags
            w.Write(FTagHeader.Flags);
            // Keep position in mind to calculate final size and come back here to write it
            tagSizePos = w.BaseStream.Position;
            w.Write((int)0); // Tag size placeholder to be rewritten in a few lines

            result = writeExtHeaderAndFrames(ref tag, w);

            // Record final(*) size of tag into "tag size" field of header
            // (*) : Spec clearly states that the tag final size is tag size after unsynchronization
            long finalTagPos = w.BaseStream.Position;
            w.BaseStream.Seek(tagSizePos, SeekOrigin.Begin);
            tagSize = (int)(finalTagPos - tagSizePos - 4);
            w.Write(StreamUtils.EncodeSynchSafeInt32(tagSize));

            if (ID3v2_useExtendedHeaderRestrictions)
            {
                if (tagSize/1024 > FTagHeader.TagSizeRestrictionKB)
                {
                    LogDelegator.GetLogDelegate()(Log.LV_WARNING, "Tag is too large (" + tagSize/1024 + "KB) according to ID3v2 restrictions (" + FTagHeader.TagSizeRestrictionKB + ") !");
                }
            }

            return result;
        }


        // TODO : Write ID3v2.4 footer
        // TODO : check date field format (YYYY, DDMM, timestamp)

        private bool writeExtHeaderAndFrames(ref TagData tag, BinaryWriter w)
        {
            bool result = true;
            int nbFrames = 0;
            bool doWritePicture;
            Encoding tagEncoding = Encoding.UTF8; // TODO make this customizable

            // Rewrites extended header as is
            if (FTagHeader.HasExtendedHeader)
            {
                w.Write(StreamUtils.EncodeSynchSafeInt(FTagHeader.ExtendedHeaderSize,4));
                w.Write((byte)1); // Number of flag bytes; always 1 according to spec
                w.Write(FTagHeader.ExtendedFlags);
                // TODO : calculate a new CRC according to actual tag contents instead of rewriting CRC as is
                if (FTagHeader.CRC > 0) w.Write(StreamUtils.EncodeSynchSafeInt(FTagHeader.CRC, 5));
                if (FTagHeader.TagRestrictions > 0) w.Write(FTagHeader.TagRestrictions);

                if (ID3v2_useExtendedHeaderRestrictions)
                {
                    // Force UTF-8 if encoding restriction is enabled and current encoding is not among authorized types
                    // TODO : make target format customizable (UTF-8 or ISO-8859-1)
                    if (FTagHeader.HasTextEncodingRestriction)
                    {
                        if (!(tagEncoding.BodyName.Equals("iso-8859-1") || tagEncoding.BodyName.Equals("utf-8")))
                        {
                            tagEncoding = Encoding.UTF8;
                        }
                    }
                }
            }

            // === ID3v2 FRAMES ===
            IDictionary<byte, String> map = tag.ToMap();

            // Supported textual fields
            foreach (byte frameType in map.Keys)
            {
                foreach(string s in frameMapping_v23_24.Keys)
                {
                    if (frameType == frameMapping_v23_24[s])
                    {
                        writeTextFrame(ref w, s, map[frameType], tagEncoding);
                        nbFrames++;
                        break;
                    }
                }
            }

            // Other textual fields
            foreach (TagData.MetaFieldInfo fieldInfo in tag.AdditionalFields)
            {
                if (fieldInfo.TagType.Equals(getImplementedTagType()) && !fieldInfo.MarkedForDeletion)
                {
                    writeTextFrame(ref w, fieldInfo.NativeFieldCode, fieldInfo.Value, tagEncoding);
                    nbFrames++;
                }
            }

            foreach (TagData.PictureInfo picInfo in tag.Pictures)
            {
                // Picture has either to be supported, or to come from the right tag standard
                doWritePicture = !picInfo.PicType.Equals(TagData.PIC_TYPE.Unsupported);
                if (!doWritePicture) doWritePicture =  (getImplementedTagType() == picInfo.TagType);
                // It also has not to be marked for deletion
                doWritePicture = doWritePicture && (!picInfo.MarkedForDeletion);

                if (doWritePicture)
                {
                    writePictureFrame(ref w, picInfo.PictureData, picInfo.NativeFormat, Utils.GetMimeTypeFromImageFormat(picInfo.NativeFormat), picInfo.PicType.Equals(TagData.PIC_TYPE.Unsupported) ? (byte)picInfo.NativePicCode : EncodeID3v2PictureType(picInfo.PicType), "", tagEncoding);
                    nbFrames++;
                }
            }

            if (ID3v2_useExtendedHeaderRestrictions)
            {
                if (nbFrames > FTagHeader.TagFramesRestriction)
                {
                    LogDelegator.GetLogDelegate()(Log.LV_WARNING, "Tag has too many frames ("+ nbFrames +") according to ID3v2 restrictions ("+ FTagHeader.TagFramesRestriction +") !");
                }
            }

            return result;
        }

        private void writeTextFrame(ref BinaryWriter writer, String frameCode, String text, Encoding tagEncoding)
        {
            string actualFrameCode; // Used for writing TXXX frames
            long frameSizePos;
            long finalFramePos;
            long frameOffset;
            int frameHeaderSize = 6; // 4-byte size + 2-byte flags

            bool writeFieldValue = true;
            bool writeFieldEncoding = true;
            bool writeNullTermination = false;

            BinaryWriter w;
            MemoryStream s = null;

            if (FTagHeader.UsesUnsynchronisation)
            {
                s = new MemoryStream(Size);
                w = new BinaryWriter(s, tagEncoding);
                frameOffset = writer.BaseStream.Position;
            } else {
                w = writer;
                frameOffset = 0;
            }

            if (ID3v2_useExtendedHeaderRestrictions)
            {
                if (text.Length > FTagHeader.TextFieldSizeRestriction)
                {
                    LogDelegator.GetLogDelegate()(Log.LV_INFO, frameCode + " field value (" + text + ") is longer than authorized by ID3v2 restrictions; reducing to " + FTagHeader.TextFieldSizeRestriction + " characters");

                    text = text.Substring(0, FTagHeader.TextFieldSizeRestriction);
                }
            }

            frameCode = frameCode.ToUpper();
            actualFrameCode = frameCode;

            // If frame is not standard, it has to be added through TXXX frame ("user-defined text information frame")
            if (!standardFrames_v24.Contains(frameCode))
            {
                frameCode = "TXXX";
            }

            w.Write(frameCode.ToCharArray());
            frameSizePos = w.BaseStream.Position;
            w.Write((int)0); // Frame size placeholder to be rewritten in a few lines

            // TODO : handle frame flags (See ID3v2.4 spec; �4.1)
            UInt16 flags = 0;
            if (FTagHeader.UsesUnsynchronisation)
            {
                flags = 2;
            }
            w.Write(StreamUtils.ReverseUInt16(flags));

            // Comments frame specifics
            if (frameCode.Substring(0, 3).Equals("COM"))
            {
                // Encoding according to ID3v2 specs
                w.Write(encodeID3v2CharEncoding(tagEncoding));
                // Language ID (ISO-639-2)
                w.Write("eng".ToCharArray()); // TODO : handle this field dynamically
                                              // Short content description
                w.Write('\0'); // Empty string, null-terminated; TODO : handle this field dynamically

                writeFieldEncoding = false;
            }
            else if (frameCode.Substring(0,3).Equals("POP")) // Rating frame specifics
            {
                // User e-mail
                w.Write('\0'); // Empty string, null-terminated; TODO : handle this field dynamically
                               // ID3v2 rating : scale 0 to 255
                w.Write((byte)Math.Max(255, Int32.Parse(text) * 51));
                // Play count
                w.Write((int)0); // TODO : handle this field dynamically. Warning : may be longer than 32 bits (see specs)

                writeFieldValue = false;
            }
            else if (frameCode.Substring(0, 3).Equals("TXX")) // User-defined text frame specifics
            {
                if (writeFieldEncoding) w.Write(encodeID3v2CharEncoding(tagEncoding)); // Encoding according to ID3v2 specs
                w.Write(actualFrameCode.ToCharArray());
                w.Write('\0');

                writeFieldEncoding = false;
                writeNullTermination = true;
            }

            if (writeFieldValue)
            {
                if (writeFieldEncoding) w.Write(encodeID3v2CharEncoding(tagEncoding)); // Encoding according to ID3v2 specs
                w.Write(text.ToCharArray());
                if (writeNullTermination) w.Write('\0');
            }


            if (FTagHeader.UsesUnsynchronisation)
            {
                s.Seek(0, SeekOrigin.Begin);
                encodeUnsynchronizedStreamTo(s, writer);
                w.Close();
            }

            // Go back to frame size location to write its actual size 
            finalFramePos = writer.BaseStream.Position;
            writer.BaseStream.Seek(frameOffset+frameSizePos, SeekOrigin.Begin);
            writer.Write(StreamUtils.EncodeSynchSafeInt32((int)(finalFramePos - frameSizePos - frameOffset - frameHeaderSize)));
            writer.BaseStream.Seek(finalFramePos, SeekOrigin.Begin);
        }

        private void writePictureFrame(ref BinaryWriter writer, byte[] pictureData, ImageFormat picFormat, string mimeType, byte pictureTypeCode, string picDescription, Encoding tagEncoding)
        {
            // Binary tag writing management
            long frameOffset;
            long frameSizePos;
            long frameSizePos2;
            long finalFramePos;
            long finalFramePosRaw;
            int dataSizeModifier = 0;

            // Picture operations management
            Image picture = null;

            // Unsynchronization management
            BinaryWriter w;
            MemoryStream s = null;


            if (FTagHeader.UsesUnsynchronisation)
            {
                s = new MemoryStream(Size);
                w = new BinaryWriter(s, tagEncoding);
                frameOffset = writer.BaseStream.Position;
            }
            else
            {
                w = writer;
                frameOffset = 0;
            }

            w.Write("APIC".ToCharArray());

            frameSizePos = w.BaseStream.Position;
            w.Write((int)0); // Temp frame size placeholder; will be rewritten at the end of the routine

            // TODO : handle frame flags (See ID3v2.4 spec; �4.1)
            // Data size is the "natural" size of the picture (i.e. without unsynchronization) + the size of the headerless APIC frame (i.e. starting from the "text encoding" byte)
            /*
            w.Write('\0');
            w.Write('\0');
            */
            UInt16 flags = 1;
            if (FTagHeader.UsesUnsynchronisation) flags = 3;
            w.Write(StreamUtils.ReverseUInt16(flags));
            dataSizeModifier += 2;

            frameSizePos2 = w.BaseStream.Position;
            w.Write((int)0);
            dataSizeModifier += 4;

            // Beginning of APIC frame data

            w.Write(encodeID3v2CharEncoding(tagEncoding));

            // Application of ID3v2 extended header restrictions
            if (ID3v2_useExtendedHeaderRestrictions)
            {
                // Force JPEG if encoding restriction is enabled and mime-type is not among authorized types
                // TODO : make target format customizable (JPEG or PNG)
                if (FTagHeader.HasPictureEncodingRestriction)
                {
                    if (!(mimeType.ToLower().Equals("image/jpeg") || mimeType.ToLower().Equals("image/png")))
                    {
                        LogDelegator.GetLogDelegate()(Log.LV_INFO, "Embedded picture format ("+ mimeType +") does not respect ID3v2 restrictions; switching to JPEG");

                        picture = Image.FromStream(new MemoryStream(pictureData));

                        mimeType = "image/jpeg";
                        picFormat = ImageFormat.Jpeg;
                    }
                }

                // Force picture dimensions if a size restriction is enabled
                if (FTagHeader.PictureSizeRestriction > 0)
                {
                    if ( (256 == FTagHeader.PictureSizeRestriction) && ((picture.Height > 256) || (picture.Width > 256))) // 256x256 or less
                    {
                        LogDelegator.GetLogDelegate()(Log.LV_INFO, "Embedded picture format ("+ picture.Width + "x" + picture.Height +") does not respect ID3v2 restrictions (256x256 or less); resizing");

                        picture = Image.FromStream(new MemoryStream(pictureData));
                        picture = Utils.ResizeImage(picture, new System.Drawing.Size(256, 256), true);
                    }
                    else if ((64 == FTagHeader.PictureSizeRestriction) && ((picture.Height > 64) || (picture.Width > 64))) // 64x64 or less
                    {
                        LogDelegator.GetLogDelegate()(Log.LV_INFO, "Embedded picture format (" + picture.Width + "x" + picture.Height + ") does not respect ID3v2 restrictions (64x64 or less); resizing");

                        picture = Image.FromStream(new MemoryStream(pictureData));
                        picture = Utils.ResizeImage(picture, new System.Drawing.Size(64, 64), true);
                    }
                    else if ((63 == FTagHeader.PictureSizeRestriction) && ((picture.Height != 64) && (picture.Width != 64))) // exactly 64x64
                    {
                        LogDelegator.GetLogDelegate()(Log.LV_INFO, "Embedded picture format (" + picture.Width + "x" + picture.Height + ") does not respect ID3v2 restrictions (exactly 64x64); resizing");

                        picture = Image.FromStream(new MemoryStream(pictureData));
                        picture = Utils.ResizeImage(picture, new System.Drawing.Size(64, 64), false);
                    }
                }
            }

            // Null-terminated string = mime-type encoded in ISO-8859-1
            w.Write(Utils.Latin1Encoding.GetBytes(mimeType.ToCharArray())); // Force ISO-8859-1 format for mime-type
            w.Write('\0'); // String should be null-terminated

            w.Write(pictureTypeCode);

            if (picDescription.Length > 0) w.Write(picDescription); // Picture description
            w.Write('\0'); // Description should be null-terminated

            if (picture != null) // Picture has been somehow modified when checking against extended header restrictions
            {
                picture.Save(w.BaseStream, picFormat);
            } else
            {
                w.Write(pictureData);
            }

            finalFramePosRaw = w.BaseStream.Position;

            if (FTagHeader.UsesUnsynchronisation)
            {
                s.Seek(0, SeekOrigin.Begin);
                encodeUnsynchronizedStreamTo(s, writer);
                w.Close();
            }

            // Go back to frame size location to write its actual size
            finalFramePos = writer.BaseStream.Position;

            writer.BaseStream.Seek(frameSizePos + frameOffset, SeekOrigin.Begin);
            writer.Write(StreamUtils.EncodeSynchSafeInt32((int)(finalFramePos - frameSizePos - frameOffset - dataSizeModifier)));

            writer.BaseStream.Seek(frameSizePos2 + frameOffset, SeekOrigin.Begin);
            writer.Write(StreamUtils.EncodeSynchSafeInt32((int)(finalFramePosRaw - frameSizePos2 - dataSizeModifier)));

            writer.BaseStream.Seek(finalFramePos, SeekOrigin.Begin);
        }

        // ---------------------------------------------------------------------------
        // ID3v2-SPECIFIC FEATURES
        // ---------------------------------------------------------------------------

        /// <summary>
        /// Extract genre name from String according to ID3 conventions
        /// </summary>
        /// <param name="iGenre">String representation of genre according to various ID3v1/v2 conventions</param>
        /// <returns>Genre name</returns>
        private static String extractGenreFromID3v2Code(String iGenre)
        {
            if (null == iGenre) return "";

            String result = Utils.StripZeroChars(iGenre.Trim());
            int genreIndex = -1;

            // Any number between parenthesis
            Regex regex = new Regex(@"(?<=\()\d+?(?=\))");

            Match match = regex.Match(result);
            // First match is directly returned
            if (match.Success)
            {
                genreIndex = Int32.Parse(match.Value);
                // Delete genre index string from the tag value
                result = result.Remove(0, result.LastIndexOf(')') + 1);
            }

            if (("" == result) && (genreIndex != -1) && (genreIndex < ID3v1.MusicGenre.Length)) result = ID3v1.MusicGenre[genreIndex];

            return result;
        }

        // Specific to ID3v2 : extract numeric rating from POP/POPM block containing other useless/obsolete data
        private static byte readRatingInPopularityMeter(BinaryReader Source, Encoding encoding)
        {
            // Skip the e-mail, which is a null-terminated string
            StreamUtils.ReadNullTerminatedString(Source, encoding);

            // Returns the rating, contained in the following byte
            return Source.ReadByte();
        }

        // Specific to ID3v2 : read Unicode BOM and return the corresponding encoding
        // NB : This implementation only works with UTF-16 BOMs (i.e. UTF-8 and UTF-32 BOMs will not be detected)
        private BOMProperties readBOM(ref Stream fs)
        {
            BOMProperties result = new BOMProperties();
            result.Size = 1;
            result.Encoding = Encoding.Unicode;

            int b = fs.ReadByte();
            bool first = true;
            bool foundFE = false;
            bool foundFF = false;

            while (0 == b || 0xFF == b || 0xFE == b)
            {
                // All UTF-16 BOMs either start or end with 0xFE or 0xFF
                // => Having them both read means that the entirety of the UTF-16 BOM has been read
                foundFE = foundFE || (0xFE == b);
                foundFF = foundFF || (0xFF == b);
                if (foundFE & foundFF) break;

                if (first && b != 0)
                {
                    // 0xFE first means data is coded in Big Endian
                    if (0xFE == b) result.Encoding = Encoding.BigEndianUnicode;
                    first = false;
                }

                b = fs.ReadByte();
                result.Size++;
            }

            return result;
        }

        public static TagData.PIC_TYPE DecodeID3v2PictureType(int picCode)
        {
            if (0 == picCode) return TagData.PIC_TYPE.Generic;      // Spec calls it "Other"
            else if (3 == picCode) return TagData.PIC_TYPE.Front;
            else if (4 == picCode) return TagData.PIC_TYPE.Back;
            else if (6 == picCode) return TagData.PIC_TYPE.CD;
            else return TagData.PIC_TYPE.Unsupported;
        }

        public static byte EncodeID3v2PictureType(TagData.PIC_TYPE picCode)
        {
            if (TagData.PIC_TYPE.Front.Equals(picCode)) return 3;
            else if (TagData.PIC_TYPE.Back.Equals(picCode)) return 4;
            else if (TagData.PIC_TYPE.CD.Equals(picCode)) return 6;
            else return 0;
        }


        // Copies the stream while cleaning abnormalities due to unsynchronization (Cf. �5 of ID3v2.0 specs; �6 of ID3v2.3+ specs)
        // => every "0xff 0x00" becomes "0xff"
        private static void decodeUnsynchronizedStreamTo(Stream mTo, BinaryReader r, long length)
        {
            BinaryWriter w = new BinaryWriter(mTo); // This reader shouldn't be closed at the end of the function, else the stream closes as well and becomes inaccessible
            
            long effectiveLength;
            long initialPosition;
            byte prevB_2 = 0;
            byte prevB_1 = 0;
            byte b;

            initialPosition = r.BaseStream.Position;
            if (0 == length) effectiveLength = r.BaseStream.Length; else effectiveLength = length;

            while (r.BaseStream.Position < initialPosition + effectiveLength && r.BaseStream.Position < r.BaseStream.Length)
            {
                b = r.ReadByte();
                if ((0xFF == prevB_1) && (0x00 == b)) b = r.ReadByte();

                w.Write(b);
                prevB_2 = prevB_1;
                prevB_1 = b;
            }
        }

        // Copies the stream while unsynchronizing it (Cf. �5 of ID3v2.0 specs; �6 of ID3v2.3+ specs)
        // => every "0xff 0xex" becomes "0xff 0x00 0xex"; every "0xff 0x00" becomes "0xff 0x00 0x00"
        private static void encodeUnsynchronizedStreamTo(Stream mFrom, BinaryWriter w)
        {
            // TODO PERF : profile using BinaryReader.ReadByte & BinaryWriter.Write(byte) vs. Stream.ReadByte & Stream.WriteByte

            BinaryReader r = new BinaryReader(mFrom); // This reader shouldn't be closed at the end of the function, else the stream closes as well and becomes inaccessible
            
            long initialPosition;
            byte b1,b2;

            initialPosition = r.BaseStream.Position;

            b1 = r.ReadByte();
            while (r.BaseStream.Position < initialPosition + r.BaseStream.Length && r.BaseStream.Position < r.BaseStream.Length)
            {
                b2 = r.ReadByte();
                w.Write(b1);
                if (0xFF == b1 && ( (0x00 == b2) || (0xE0 == (b2 & 0xE0))))
                {
                    w.Write((byte)0);
                }
                b1 = b2;
            }
            w.Write(b1);
        }

        /// Returns the .NET Encoding corresponding to the ID3v2 convention (see below)
        ///
        /// Default encoding should be "ISO-8859-1"
        /// 
        /// Warning : due to approximative implementations, some tags seem to be coded
        /// with the default encoding of the OS they have been tagged on
        /// 
        ///  $00    ISO-8859-1 [ISO-8859-1]. Terminated with $00.
        ///  $01    UTF-16 [UTF-16] encoded Unicode [UNICODE], with BOM if version > 2.2
        ///         All strings in the same frame SHALL have the same byteorder.
        ///         Terminated with $00 00.
        ///  $02    UTF-16BE [UTF-16] encoded Unicode [UNICODE] without BOM.
        ///         Terminated with $00 00.
        ///  $03    UTF-8 [UTF-8] encoded Unicode [UNICODE]. Terminated with $00.
        private static Encoding decodeID3v2CharEncoding(byte encoding)
        {
            if (0 == encoding) return Encoding.GetEncoding("ISO-8859-1");   // aka ISO Latin-1
            else if (1 == encoding) return Encoding.Unicode;                // UTF-16 with BOM if version > 2.2
            else if (2 == encoding) return Encoding.BigEndianUnicode;       // UTF-16 Big Endian without BOM (since ID3v2.4)
            else if (3 == encoding) return Encoding.UTF8;                   // UTF-8 (since ID3v2.4)
            else return Encoding.Default;
        }

        private static byte encodeID3v2CharEncoding(Encoding encoding)
        {
            if (encoding.Equals(Encoding.Unicode)) return 1;
            else if (encoding.Equals(Encoding.BigEndianUnicode)) return 2;
            else if (encoding.Equals(Encoding.UTF8)) return 3;
            else return 0; // Default = ISO-8859-1 / ISO Latin-1
        }

    }
}