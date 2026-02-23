using System;
using System.Collections.Generic;
using System.Text;

namespace QuestTracker;

/// <summary>
/// C# port of lz-string library - compatible with JavaScript version
/// https://github.com/pieroxy/lz-string
/// </summary>
public static class LZString
{
    private const string KeyStrUriSafe = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+-$";

    private static readonly Dictionary<char, int> BaseReverseDic = new();

    static LZString()
    {
        for (int i = 0; i < KeyStrUriSafe.Length; i++)
        {
            BaseReverseDic[KeyStrUriSafe[i]] = i;
        }
    }

    public static string CompressToEncodedURIComponent(string input)
    {
        if (string.IsNullOrEmpty(input))
            return "";

        return Compress(input, 6, c => KeyStrUriSafe[c]);
    }

    public static string? DecompressFromEncodedURIComponent(string input)
    {
        if (string.IsNullOrEmpty(input))
            return "";

        input = input.Replace(' ', '+');
        return Decompress(input.Length, 32, index => BaseReverseDic[input[index]]);
    }

    private static string Compress(string uncompressed, int bitsPerChar, Func<int, char> getCharFromInt)
    {
        int i, value;
        var context_dictionary = new Dictionary<string, int>();
        var context_dictionaryToCreate = new Dictionary<string, bool>();
        var context_c = "";
        var context_wc = "";
        var context_w = "";
        var context_enlargeIn = 2;
        var context_dictSize = 3;
        var context_numBits = 2;
        var context_data = new StringBuilder();
        var context_data_val = 0;
        var context_data_position = 0;

        for (int ii = 0; ii < uncompressed.Length; ii++)
        {
            context_c = uncompressed[ii].ToString();
            if (!context_dictionary.ContainsKey(context_c))
            {
                context_dictionary[context_c] = context_dictSize++;
                context_dictionaryToCreate[context_c] = true;
            }

            context_wc = context_w + context_c;
            if (context_dictionary.ContainsKey(context_wc))
            {
                context_w = context_wc;
            }
            else
            {
                if (context_dictionaryToCreate.ContainsKey(context_w))
                {
                    if (context_w.Length > 0 && context_w[0] < 256)
                    {
                        for (i = 0; i < context_numBits; i++)
                        {
                            context_data_val <<= 1;
                            if (context_data_position == bitsPerChar - 1)
                            {
                                context_data_position = 0;
                                context_data.Append(getCharFromInt(context_data_val));
                                context_data_val = 0;
                            }
                            else
                            {
                                context_data_position++;
                            }
                        }
                        value = context_w[0];
                        for (i = 0; i < 8; i++)
                        {
                            context_data_val = (context_data_val << 1) | (value & 1);
                            if (context_data_position == bitsPerChar - 1)
                            {
                                context_data_position = 0;
                                context_data.Append(getCharFromInt(context_data_val));
                                context_data_val = 0;
                            }
                            else
                            {
                                context_data_position++;
                            }
                            value >>= 1;
                        }
                    }
                    else
                    {
                        value = 1;
                        for (i = 0; i < context_numBits; i++)
                        {
                            context_data_val = (context_data_val << 1) | value;
                            if (context_data_position == bitsPerChar - 1)
                            {
                                context_data_position = 0;
                                context_data.Append(getCharFromInt(context_data_val));
                                context_data_val = 0;
                            }
                            else
                            {
                                context_data_position++;
                            }
                            value = 0;
                        }
                        value = context_w[0];
                        for (i = 0; i < 16; i++)
                        {
                            context_data_val = (context_data_val << 1) | (value & 1);
                            if (context_data_position == bitsPerChar - 1)
                            {
                                context_data_position = 0;
                                context_data.Append(getCharFromInt(context_data_val));
                                context_data_val = 0;
                            }
                            else
                            {
                                context_data_position++;
                            }
                            value >>= 1;
                        }
                    }
                    context_enlargeIn--;
                    if (context_enlargeIn == 0)
                    {
                        context_enlargeIn = (int)Math.Pow(2, context_numBits);
                        context_numBits++;
                    }
                    context_dictionaryToCreate.Remove(context_w);
                }
                else
                {
                    value = context_dictionary[context_w];
                    for (i = 0; i < context_numBits; i++)
                    {
                        context_data_val = (context_data_val << 1) | (value & 1);
                        if (context_data_position == bitsPerChar - 1)
                        {
                            context_data_position = 0;
                            context_data.Append(getCharFromInt(context_data_val));
                            context_data_val = 0;
                        }
                        else
                        {
                            context_data_position++;
                        }
                        value >>= 1;
                    }
                }
                context_enlargeIn--;
                if (context_enlargeIn == 0)
                {
                    context_enlargeIn = (int)Math.Pow(2, context_numBits);
                    context_numBits++;
                }
                context_dictionary[context_wc] = context_dictSize++;
                context_w = context_c;
            }
        }

        if (!string.IsNullOrEmpty(context_w))
        {
            if (context_dictionaryToCreate.ContainsKey(context_w))
            {
                if (context_w[0] < 256)
                {
                    for (i = 0; i < context_numBits; i++)
                    {
                        context_data_val <<= 1;
                        if (context_data_position == bitsPerChar - 1)
                        {
                            context_data_position = 0;
                            context_data.Append(getCharFromInt(context_data_val));
                            context_data_val = 0;
                        }
                        else
                        {
                            context_data_position++;
                        }
                    }
                    value = context_w[0];
                    for (i = 0; i < 8; i++)
                    {
                        context_data_val = (context_data_val << 1) | (value & 1);
                        if (context_data_position == bitsPerChar - 1)
                        {
                            context_data_position = 0;
                            context_data.Append(getCharFromInt(context_data_val));
                            context_data_val = 0;
                        }
                        else
                        {
                            context_data_position++;
                        }
                        value >>= 1;
                    }
                }
                else
                {
                    value = 1;
                    for (i = 0; i < context_numBits; i++)
                    {
                        context_data_val = (context_data_val << 1) | value;
                        if (context_data_position == bitsPerChar - 1)
                        {
                            context_data_position = 0;
                            context_data.Append(getCharFromInt(context_data_val));
                            context_data_val = 0;
                        }
                        else
                        {
                            context_data_position++;
                        }
                        value = 0;
                    }
                    value = context_w[0];
                    for (i = 0; i < 16; i++)
                    {
                        context_data_val = (context_data_val << 1) | (value & 1);
                        if (context_data_position == bitsPerChar - 1)
                        {
                            context_data_position = 0;
                            context_data.Append(getCharFromInt(context_data_val));
                            context_data_val = 0;
                        }
                        else
                        {
                            context_data_position++;
                        }
                        value >>= 1;
                    }
                }
                context_enlargeIn--;
                if (context_enlargeIn == 0)
                {
                    context_enlargeIn = (int)Math.Pow(2, context_numBits);
                    context_numBits++;
                }
                context_dictionaryToCreate.Remove(context_w);
            }
            else
            {
                value = context_dictionary[context_w];
                for (i = 0; i < context_numBits; i++)
                {
                    context_data_val = (context_data_val << 1) | (value & 1);
                    if (context_data_position == bitsPerChar - 1)
                    {
                        context_data_position = 0;
                        context_data.Append(getCharFromInt(context_data_val));
                        context_data_val = 0;
                    }
                    else
                    {
                        context_data_position++;
                    }
                    value >>= 1;
                }
            }
            context_enlargeIn--;
            if (context_enlargeIn == 0)
            {
                context_enlargeIn = (int)Math.Pow(2, context_numBits);
                context_numBits++;
            }
        }

        value = 2;
        for (i = 0; i < context_numBits; i++)
        {
            context_data_val = (context_data_val << 1) | (value & 1);
            if (context_data_position == bitsPerChar - 1)
            {
                context_data_position = 0;
                context_data.Append(getCharFromInt(context_data_val));
                context_data_val = 0;
            }
            else
            {
                context_data_position++;
            }
            value >>= 1;
        }

        while (true)
        {
            context_data_val <<= 1;
            if (context_data_position == bitsPerChar - 1)
            {
                context_data.Append(getCharFromInt(context_data_val));
                break;
            }
            else
            {
                context_data_position++;
            }
        }

        return context_data.ToString();
    }

    private static string? Decompress(int length, int resetValue, Func<int, int> getNextValue)
    {
        var dictionary = new Dictionary<int, string>();
        int enlargeIn = 4;
        int dictSize = 4;
        int numBits = 3;
        string entry;
        var result = new StringBuilder();
        string w;
        int bits, resb, maxpower, power;
        string c = "";

        var data_val = getNextValue(0);
        var data_position = resetValue;
        var data_index = 1;

        for (int i = 0; i < 3; i++)
        {
            dictionary[i] = ((char)i).ToString();
        }

        bits = 0;
        maxpower = (int)Math.Pow(2, 2);
        power = 1;
        while (power != maxpower)
        {
            resb = data_val & data_position;
            data_position >>= 1;
            if (data_position == 0)
            {
                data_position = resetValue;
                data_val = getNextValue(data_index++);
            }
            bits |= (resb > 0 ? 1 : 0) * power;
            power <<= 1;
        }

        switch (bits)
        {
            case 0:
                bits = 0;
                maxpower = (int)Math.Pow(2, 8);
                power = 1;
                while (power != maxpower)
                {
                    resb = data_val & data_position;
                    data_position >>= 1;
                    if (data_position == 0)
                    {
                        data_position = resetValue;
                        data_val = getNextValue(data_index++);
                    }
                    bits |= (resb > 0 ? 1 : 0) * power;
                    power <<= 1;
                }
                c = ((char)bits).ToString();
                break;
            case 1:
                bits = 0;
                maxpower = (int)Math.Pow(2, 16);
                power = 1;
                while (power != maxpower)
                {
                    resb = data_val & data_position;
                    data_position >>= 1;
                    if (data_position == 0)
                    {
                        data_position = resetValue;
                        data_val = getNextValue(data_index++);
                    }
                    bits |= (resb > 0 ? 1 : 0) * power;
                    power <<= 1;
                }
                c = ((char)bits).ToString();
                break;
            case 2:
                return "";
        }
        dictionary[3] = c;
        w = c;
        result.Append(c);

        while (true)
        {
            if (data_index > length)
            {
                return "";
            }

            bits = 0;
            maxpower = (int)Math.Pow(2, numBits);
            power = 1;
            while (power != maxpower)
            {
                resb = data_val & data_position;
                data_position >>= 1;
                if (data_position == 0)
                {
                    data_position = resetValue;
                    data_val = getNextValue(data_index++);
                }
                bits |= (resb > 0 ? 1 : 0) * power;
                power <<= 1;
            }

            int cc = bits;
            switch (cc)
            {
                case 0:
                    bits = 0;
                    maxpower = (int)Math.Pow(2, 8);
                    power = 1;
                    while (power != maxpower)
                    {
                        resb = data_val & data_position;
                        data_position >>= 1;
                        if (data_position == 0)
                        {
                            data_position = resetValue;
                            data_val = getNextValue(data_index++);
                        }
                        bits |= (resb > 0 ? 1 : 0) * power;
                        power <<= 1;
                    }
                    dictionary[dictSize++] = ((char)bits).ToString();
                    cc = dictSize - 1;
                    enlargeIn--;
                    break;
                case 1:
                    bits = 0;
                    maxpower = (int)Math.Pow(2, 16);
                    power = 1;
                    while (power != maxpower)
                    {
                        resb = data_val & data_position;
                        data_position >>= 1;
                        if (data_position == 0)
                        {
                            data_position = resetValue;
                            data_val = getNextValue(data_index++);
                        }
                        bits |= (resb > 0 ? 1 : 0) * power;
                        power <<= 1;
                    }
                    dictionary[dictSize++] = ((char)bits).ToString();
                    cc = dictSize - 1;
                    enlargeIn--;
                    break;
                case 2:
                    return result.ToString();
            }

            if (enlargeIn == 0)
            {
                enlargeIn = (int)Math.Pow(2, numBits);
                numBits++;
            }

            if (dictionary.ContainsKey(cc))
            {
                entry = dictionary[cc];
            }
            else
            {
                if (cc == dictSize)
                {
                    entry = w + w[0];
                }
                else
                {
                    return null;
                }
            }
            result.Append(entry);

            dictionary[dictSize++] = w + entry[0];
            enlargeIn--;

            if (enlargeIn == 0)
            {
                enlargeIn = (int)Math.Pow(2, numBits);
                numBits++;
            }

            w = entry;
        }
    }
}
