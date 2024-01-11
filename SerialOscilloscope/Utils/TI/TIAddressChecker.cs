using System.Text.RegularExpressions;
using System.Xml.Linq;
using System.Xml.XPath;

namespace SerialOscilloscope.Utils.TI
{
    public class TIAddressChecker
    {

        public void LoadDwarfXml(string filepath = "")
        {
            if (filepath.Length == 0)
            {
                filepath = DwarfXmlPath;
            }
            else
            {
                DwarfXmlPath = filepath;
            }

            _dwarfInfo = XDocument.Load(filepath);
        }

        public bool IsDwarfXmlLoaded()
        {
            return _dwarfInfo != null;
        }

        public void UnloadDwarfXml()
        {
            _dwarfInfo = null;
        }

        /// <summary>
        /// 裁剪DWARF.xml文件
        /// </summary>
        /// <param name="dwarfXmlPath"></param>
        /// <exception cref="Exception"></exception>
        public static void TrimDwarfXml(string dwarfXmlPath)
        {
            try
            {
                XDocument fullXml = XDocument.Load(dwarfXmlPath);
                var dwarfInfo = fullXml.XPathSelectElement("//dwarf");
                var dwarfSections = dwarfInfo.Elements("section").ToList();
                for (int i = 0; i < dwarfSections.Count; i++)
                {
                    var secName = dwarfSections[i].Element("name")?.Value;
                    if (secName == ".debug_info" ||
                        secName == ".debug_pubnames")
                    {
                        // 目前只需要".debug_info"和".debug_pubnames"两部分
                        continue;
                    }

                    dwarfSections[i].Remove();
                }

                var dwarfDoc = new XDocument();
                dwarfDoc.Add(dwarfInfo);
                dwarfDoc.Save(dwarfXmlPath);
            }
            catch
            {
                throw new Exception("There may be something wrong with the DWARF.xml");
            }
        }


        /// <summary>
        /// 根据变量名在DWARF.xml文件中查找其地址
        /// </summary>
        /// <param name="varName"></param>
        /// <param name="sn"></param>
        /// <returns></returns>
        public UInt32 SearchAddressByName(string varName, int id = 1)
        {   
            if (_dwarfInfo == null)
            {
                throw new InvalidOperationException("No COFF(.out) or DWARF(.xml) file was loaded.");
            }

            try
            {
                if (varName.StartsWith("0x") || varName.StartsWith("0X"))
                {
                    UInt32 addr = Convert.ToUInt32(varName, 16);
                    return addr;
                }
                VariableInfo varInfo = ParseVariableName(varName);
                UInt32 baseAddr  = GetBaseAddress(varInfo);
               
                UInt32 membersOffsets = 0;
                for (int i = 1; i < varInfo.SplitNames.Count; i++)
                {
                    membersOffsets += GetMemberOffset(varInfo, i);
                    if (varInfo.IsArrayExpression[i])
                        membersOffsets += GetArrayAddressOffset(varInfo, i);
                }

                return baseAddr + membersOffsets;
            }
            catch (FormatException fe)
            {
                throw new Exception($"变量{id}: '{varName}'" + "\n" +
                                          "    变量格式错误: " + fe.Message + "\r\n\n");
            }
            
            catch (Exception ex)
            {
                throw new Exception($"变量{id}: '{varName}'" + "\n" +
                                    "     寻址异常信息: " + ex.Message + "\r\n\n");
            }

        }

        private VariableInfo ParseVariableName(string varName)
        {
            if (string.IsNullOrWhiteSpace(varName))
            {
                throw new FormatException("Variable Name is Null.");
            }

            var varInfo = new VariableInfo() { FullName = varName };

            // 结构变量分割
            if (varName.IndexOf('.') > 0)
            { // 带'.'结构层次变量
                string subName;
                string remains = varName;
                int dotIdx = remains.IndexOf('.');
                while (dotIdx > 0)
                {
                    subName = remains.Substring(0, dotIdx);
                    remains = remains.Substring(dotIdx + 1);
                    dotIdx = remains.IndexOf('.');
                    varInfo.SplitNames.Add(subName);
                }

                varInfo.SplitNames.Add(remains);

            }
            else
            {  // 普通变量
                varInfo.SplitNames.Add(varName);
            }

            // 处理变量子名称
            for (int i = 0; i < varInfo.SplitNames.Count; i++)
            {
                string subName = varInfo.SplitNames[i];

                if (string.IsNullOrWhiteSpace(subName))
                    throw new FormatException("Invalid Variable Name.");

                // 分割名称与数组索引
                int leftBraceIdx, rightBraceIdx;
                string namePart = subName;
                string bracePart = "";
                if ((leftBraceIdx = subName.IndexOf('[')) > 0)
                {
                    namePart = subName.Substring(0, leftBraceIdx);
                    bracePart = subName.Substring(leftBraceIdx);
                }

                // C语言变量命名规则检查：字母、数字、下划线，首字不能为数字，单字只能为字母
                if (namePart.Length == 1 && !Char.IsLetter(namePart[0]))
                {
                    throw new FormatException("Invalid Variable Name.");
                }
                for (int p = 0; p < namePart.Length; p++)
                {
                    if (!(Char.IsLetter(namePart[p]) || Char.IsDigit(namePart[p]) || namePart[p] == '_')
                        || (p == 0 && Char.IsDigit(namePart[p]))
                        )
                        throw new FormatException("Invalid Variable Name.");
                }

                // 处理数组表达式
                if (bracePart.Length > 0)
                {
                    Regex squareBraceRegex = new Regex(@"(?is)(?<=\[)\s*(\d+)\s*(?=\])");  // [0-9+]
                    MatchCollection braceMatchRes = squareBraceRegex.Matches(bracePart);
                    int braceMatchStrLen = 0;
                    if (braceMatchRes.Count > 0)
                    {
                        List<UInt32> offsets = new List<UInt32>();
                        foreach (var match in braceMatchRes.ToList())
                        {
                            // 提取各维索引值
                            offsets.Add(UInt32.Parse(match.Groups[1].Value));
                            braceMatchStrLen += (match.Groups[0].Value.Length + 2);
                        }

                        if (braceMatchStrLen != bracePart.Length)
                        {
                            // 有剩余匹配失败的字符
                            throw new FormatException("Invalid Array Expression.");
                        }
                        varInfo.ArraySuboffets.Add(offsets);
                        varInfo.IsArrayExpression.Add(true);
                    }
                    else
                    {
                        throw new FormatException("Invalid Array Expression.");
                    }
                }
                else
                {
                    varInfo.ArraySuboffets.Add(new List<UInt32>(1) { 0 });
                    varInfo.IsArrayExpression.Add(false);
                }

                varInfo.SplitNames[i] = namePart;  // 只保留变量名部分

            }

            // 记录数据类型索引方便后续的寻址
            for (int i = 0; i < varInfo.SplitNames.Count; i++)
            {
                varInfo.DataTypeIds.Add("");
            }

            return varInfo;
        }

        private UInt32 GetBaseAddress(VariableInfo varInfo)
        {
            UInt32 baseAddr = 0, arrayOffset = 0;
            string baseName = varInfo.GetBaseName();
            var pubnamesSec = _dwarfInfo.XPathSelectElement(".//section[name='.debug_pubnames']");
            var infoSec = _dwarfInfo.XPathSelectElement(".//section[name='.debug_info']");

            XElement? dieBase, addrBlock;
            // 全局变量起始地址的DIE在debug_pubnames部分中直接找出
            var dieNameRef = pubnamesSec?.XPathSelectElement($".//die_name[text()='{baseName}']");
            if (dieNameRef != null)
            {
                string idref = dieNameRef.XPathSelectElement("../ref")?.Attribute("idref")?.Value;
                dieBase = infoSec?.XPathSelectElement($".//die[@id='{idref}']");
                addrBlock = dieBase?.XPathSelectElement(".//block[contains(text(), 'DW_OP_addr')]");
            }
            else
            {  // 局部变量?起始地址的DIE则尝试在debug_info中寻找
                dieBase = infoSec.XPathSelectElement($".//die[attribute[value[string[text()='{baseName}']]]]");
                addrBlock = dieBase?.XPathSelectElement(".//block[contains(text(), 'DW_OP_addr')]");
            }

            if (dieBase == null || addrBlock == null)
            {
               throw new Exception("Failed to find the base address. Ensure the variable name is mapped with its corresponding COFF(.out)");
            }
            baseAddr = Convert.ToUInt32(addrBlock?.Value.Substring("DW_OP_addr ".Length), 16);

            // 记录数据类型索引(第一层)方便后续的寻址
            var dataTypeElem = dieBase?.XPathSelectElement("./attribute[type[text()='DW_AT_type']]");
            string dataTypeId = dataTypeElem?.XPathSelectElement("./value/ref")?.Attribute("idref")?.Value;
            varInfo.DataTypeIds[0] = dataTypeId;
            

            // 如果是数组，则继续处理偏移量
            if (varInfo.IsArrayExpression[0])
            {
                arrayOffset = GetArrayAddressOffset(varInfo, 0);
            }
            return (baseAddr + arrayOffset);
        }

        private UInt32 GetMemberOffset(VariableInfo varInfo, int index, int maxDepth=20)
        {
            if (varInfo.SplitNames.Count <= 1) return 0;

            var infoSec = _dwarfInfo.XPathSelectElement(".//section[name='.debug_info']");
            var memberName = varInfo.SplitNames[index];

            UInt32 offset = 0;
            var parentTypeId = varInfo.DataTypeIds[index - 1];

            int maxCnt = 0;
            do
            {
                // 逐层寻找对应的'DW_TAG_member'，偏移值在'DW_OP_plus_uconst'
                XElement typeDie = infoSec?.XPathSelectElement($"//die[@id='{parentTypeId}']");
                XElement memberDie = typeDie?.XPathSelectElement("./die[tag[text()='DW_TAG_member'] " +
                                                           $"and attribute[value[string[text()='{memberName}']]] ]");
                if (memberDie != null)
                {
                    var offsetBlock = memberDie.XPathSelectElement(".//block[contains(text(), 'DW_OP_plus_uconst')]");

                    offset = Convert.ToUInt32(offsetBlock?.Value.Substring("DW_OP_plus_uconst ".Length), 16);

                    var memberDataTypeElem = memberDie.XPathSelectElement("./attribute[type[text()='DW_AT_type']]");
                    var memberTypeId = memberDataTypeElem?.XPathSelectElement("./value/ref")?.Attribute("idref")?.Value;
                    varInfo.DataTypeIds[index] = memberTypeId;
                    break;
                }

                XElement dataTypeElem = typeDie.XPathSelectElement("./attribute[type[text()='DW_AT_type']]");
                parentTypeId = dataTypeElem?.XPathSelectElement("./value/ref")?.Attribute("idref")?.Value;

                maxCnt++;
            } while (maxCnt < maxDepth);
            if (maxCnt >= maxDepth)
            {
                throw new Exception("Failed to find the address of the member named " + $"'{varInfo.SplitNames[index]}'.");
            }

            return offset;
        }

        private UInt32 GetArrayAddressOffset(VariableInfo varInfo, int index, int maxDepth = 20)
        {
            // 通过TypeDIE找到数组长度
            var infoSec = _dwarfInfo.XPathSelectElement(".//section[name='.debug_info']");
            XElement arrayDie = infoSec.XPathSelectElement($"//die[@id='{varInfo.DataTypeIds[index]}' and tag[text()='DW_TAG_array_type']]");

            XElement byteSizeElem = arrayDie.XPathSelectElement("./attribute[type[text()='DW_AT_byte_size']]");
            string sizeValue = byteSizeElem?.XPathSelectElement("./value/const")?.Value;
            UInt32 arrayLen = Convert.ToUInt32(sizeValue, 16);


            // 多维数组有多个子长度subrange
            List<UInt32> subrange = new List<UInt32>();
            var subRangeDies = arrayDie.XPathSelectElements("./die[tag[text()='DW_TAG_subrange_type']]");
            foreach (var elem in subRangeDies)
            {
                XElement? subrangeElem = elem.XPathSelectElement("./attribute[type[text()='DW_AT_upper_bound']]")?
                                               .XPathSelectElement("./value/const");
                UInt32 rangeValue = Convert.ToUInt32(subrangeElem?.Value, 16);
                subrange.Add(rangeValue);

            }
            // 多维数组展开，计算索引偏移量
            UInt32 units = 1, offsets = 0;
            for (int i = 0; i < varInfo.ArraySuboffets[index].Count; ++i)
            {
                for (int j = i + 1; j < varInfo.ArraySuboffets[index].Count; ++j)
                {
                    units *= subrange[j] + 1;
                }
                offsets += units * varInfo.ArraySuboffets[index][i];
            }

            // 寻找数组的数据类型大小，计算地址偏移量
            UInt32 addrOffset = 0;
            var dataTypeElem = arrayDie.XPathSelectElement("./attribute[type[text()='DW_AT_type']]");
            string dataTypeId = dataTypeElem?.XPathSelectElement("./value/ref")?.Attribute("idref")?.Value;

            int maxCnt = 0;
            do
            {
                // 逐层寻找'DW_AT_byte_size'
                XElement typeDie = infoSec.XPathSelectElement($"//die[@id='{dataTypeId}']");
                XElement dataSizeElem = typeDie.XPathSelectElement("./attribute[type[text()='DW_AT_byte_size']]");
                if (dataSizeElem != null)
                {
                    string dataSizeValue = dataSizeElem.XPathSelectElement("./value/const")?.Value;
                    UInt32 arrayDataSize = Convert.ToUInt32(dataSizeValue, 16);
                    addrOffset = arrayDataSize * offsets;
                    break;
                }

                dataTypeElem = typeDie.XPathSelectElement("./attribute[type[text()='DW_AT_type']]");
                dataTypeId = dataTypeElem?.XPathSelectElement("./value/ref")?.Attribute("idref")?.Value;

                maxCnt++;
            } while (maxCnt < maxDepth);

            if (maxCnt >= maxDepth)
            {
                throw new Exception("Failed to find the data type of the array named " + $"'{varInfo.SplitNames[index]}'.");
            }

            if (addrOffset > arrayLen - 1)
            {
                // 越界访问
                throw new Exception("An out-of-range index is assigned to access the array named " + $"'{varInfo.SplitNames[index]}'.");
            }

            return addrOffset;
        }


        public string DwarfXmlPath { get; set; }
        public string CoffPath { get; set; } = "";

        private XDocument _dwarfInfo;

    }

    internal class VariableInfo
    {
        public string FullName { get; set; }
        public List<string> SplitNames { get; set; } = new List<string>();
        public List<bool> IsArrayExpression { get; set; } = new List<bool>();
        public List<List<UInt32>> ArraySuboffets { get; set; } = new List<List<uint>>();
        public List<string> DataTypeIds { get; set; } = new List<string>();

        public string GetBaseName()
        {
            if (SplitNames.Count > 0)
            {
                return SplitNames[0];
            }
            return "";
        }

        /*public void PrintInfo() {
            // For debug
            Console.WriteLine($"\n=========\"{FullName}\" VariableInfo==========");
            Console.Write("SplitNames:");
            for (int i = 0; i < SplitNames.Count; i++) {
                Console.Write($"{SplitNames[i]}");
                if (i != SplitNames.Count - 1)
                    Console.Write("--");
            }

            Console.Write("\nIsArray:");
            for (int i = 0; i < IsArrayExpression.Count; i++) {
                Console.Write($"{IsArrayExpression[i]}");
                if (i != IsArrayExpression.Count - 1)
                    Console.Write("--");
            }
            Console.Write("\nSuboffets:");
            for (int i = 0; i < ArraySuboffets.Count; i++) {
                for (int j = 0; j < ArraySuboffets[i].Count; j++) {
                    Console.Write($"{ArraySuboffets[i][j]}");
                    if (j != ArraySuboffets[i].Count - 1)
                        Console.Write("_");
                }
                if (i != ArraySuboffets.Count - 1)
                    Console.Write("--");
            }

            Console.Write("\nDataTypeIDs:");
            for (int i = 0; i < DataTypeIds.Count; i++) {
                Console.Write($"{DataTypeIds[i]}");
                if (i != DataTypeIds.Count - 1)
                    Console.Write("--");
            }
            Console.WriteLine($"\n=========\"{FullName}\" VariableInfo==========\n");

        }*/
    }
}
