using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace WindowsDynamicBar.Core.Search;

public static class ExpressionEvaluator
{
    private static readonly Dictionary<string, double> LengthToMeters = new(StringComparer.OrdinalIgnoreCase)
    {
        { "m", 1.0 }, { "meter", 1.0 }, { "meters", 1.0 },
        { "km", 1000.0 }, { "kilometer", 1000.0 }, { "kilometers", 1000.0 },
        { "cm", 0.01 }, { "centimeter", 0.01 }, { "centimeters", 0.01 },
        { "mm", 0.001 }, { "millimeter", 0.001 }, { "millimeters", 0.001 },
        { "mi", 1609.344 }, { "mile", 1609.344 }, { "miles", 1609.344 },
        { "yd", 0.9144 }, { "yard", 0.9144 }, { "yards", 0.9144 },
        { "ft", 0.3048 }, { "foot", 0.3048 }, { "feet", 0.3048 },
        { "in", 0.0254 }, { "inch", 0.0254 }, { "inches", 0.0254 },
    };

    private static readonly Dictionary<string, double> WeightToGrams = new(StringComparer.OrdinalIgnoreCase)
    {
        { "g", 1.0 }, { "gram", 1.0 }, { "grams", 1.0 },
        { "kg", 1000.0 }, { "kilogram", 1000.0 }, { "kilograms", 1000.0 },
        { "mg", 0.001 }, { "milligram", 0.001 }, { "milligrams", 0.001 },
        { "lb", 453.59237 }, { "lbs", 453.59237 }, { "pound", 453.59237 }, { "pounds", 453.59237 },
        { "oz", 28.349523 }, { "ounce", 28.349523 }, { "ounces", 28.349523 },
    };

    private static readonly Dictionary<string, double> DataToBytes = new(StringComparer.OrdinalIgnoreCase)
    {
        { "b", 1.0 }, { "byte", 1.0 }, { "bytes", 1.0 },
        { "kb", 1024.0 }, { "kilobyte", 1024.0 }, { "kilobytes", 1024.0 },
        { "mb", 1024.0 * 1024.0 }, { "megabyte", 1024.0 * 1024.0 }, { "megabytes", 1024.0 * 1024.0 },
        { "gb", 1024.0 * 1024.0 * 1024.0 }, { "gigabyte", 1024.0 * 1024.0 * 1024.0 }, { "gigabytes", 1024.0 * 1024.0 * 1024.0 },
        { "tb", 1024.0 * 1024.0 * 1024.0 * 1024.0 }, { "terabyte", 1024.0 * 1024.0 * 1024.0 * 1024.0 },
    };

    private static readonly Dictionary<string, double> TimeToSeconds = new(StringComparer.OrdinalIgnoreCase)
    {
        { "s", 1.0 }, { "sec", 1.0 }, { "second", 1.0 }, { "seconds", 1.0 },
        { "m", 60.0 }, { "min", 60.0 }, { "minute", 60.0 }, { "minutes", 60.0 },
        { "h", 3600.0 }, { "hr", 3600.0 }, { "hour", 3600.0 }, { "hours", 3600.0 },
        { "d", 86400.0 }, { "day", 86400.0 }, { "days", 86400.0 },
    };

    private static readonly Dictionary<string, double> CurrencyToUsd = new(StringComparer.OrdinalIgnoreCase)
    {
        { "usd", 1.0 }, { "eur", 1.09 }, { "gbp", 1.28 }, { "jpy", 0.0066 },
        { "inr", 0.012 }, { "cad", 0.73 }, { "aud", 0.65 }, { "chf", 1.13 }, { "cny", 0.14 }
    };

    public static (bool Success, string Result, string Subtitle) TryEvaluate(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return (false, "", "");
        string trimmed = input.Trim();

        // 1. Try unit/currency conversion
        var conv = TryConvert(trimmed);
        if (conv.HasValue)
        {
            return (true, conv.Value.Result, conv.Value.Subtitle);
        }

        // 2. Try math evaluation
        // Check if string contains math characters (must contain at least one operator or math function)
        if (!HasMathSymbols(trimmed)) return (false, "", "");

        try
        {
            var p = new Parser(trimmed);
            double val = p.Parse();
            if (double.IsNaN(val) || double.IsInfinity(val)) return (false, "", "");

            string formatted = val % 1 == 0 && Math.Abs(val) < 1e12
                ? ((long)val).ToString("N0", CultureInfo.InvariantCulture)
                : val.ToString("G8", CultureInfo.InvariantCulture);

            return (true, formatted, $"= {formatted} · {trimmed}");
        }
        catch
        {
            return (false, "", "");
        }
    }

    private static bool HasMathSymbols(string s)
    {
        // Don't evaluate plain words or single numbers
        bool hasOp = false;
        bool hasDigit = false;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (char.IsDigit(c)) hasDigit = true;
            else if (c is '+' or '-' or '*' or '/' or '^' or '%' or '(' or ')') hasOp = true;
        }

        if (hasDigit && hasOp) return true;
        if (s.StartsWith("sqrt", StringComparison.OrdinalIgnoreCase) ||
            s.StartsWith("sin", StringComparison.OrdinalIgnoreCase) ||
            s.StartsWith("cos", StringComparison.OrdinalIgnoreCase) ||
            s.StartsWith("tan", StringComparison.OrdinalIgnoreCase) ||
            s.StartsWith("log", StringComparison.OrdinalIgnoreCase) ||
            s.StartsWith("abs", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private static (string Result, string Subtitle)? TryConvert(string input)
    {
        // Pattern: [value] [unit1] in/to [unit2]
        var match = Regex.Match(input, @"^([0-9]+(?:\.[0-9]+)?)\s*([a-zA-Z]+)\s+(?:to|in)\s+([a-zA-Z]+)$", RegexOptions.IgnoreCase);
        if (!match.Success) return null;

        if (!double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double val))
            return null;

        string uFrom = match.Groups[2].Value.ToLowerInvariant();
        string uTo = match.Groups[3].Value.ToLowerInvariant();

        // Length
        if (LengthToMeters.TryGetValue(uFrom, out double mFrom) && LengthToMeters.TryGetValue(uTo, out double mTo))
        {
            double res = val * mFrom / mTo;
            return ($"{res:G6} {uTo}", $"Length: {val} {uFrom} → {uTo}");
        }

        // Weight
        if (WeightToGrams.TryGetValue(uFrom, out double gFrom) && WeightToGrams.TryGetValue(uTo, out double gTo))
        {
            double res = val * gFrom / gTo;
            return ($"{res:G6} {uTo}", $"Weight: {val} {uFrom} → {uTo}");
        }

        // Temperature
        if (IsTemp(uFrom) && IsTemp(uTo))
        {
            double res = ConvertTemp(val, uFrom, uTo);
            return ($"{res:F1} °{uTo.ToUpperInvariant()[0]}", $"Temperature: {val}°{uFrom.ToUpperInvariant()[0]} → °{uTo.ToUpperInvariant()[0]}");
        }

        // Data
        if (DataToBytes.TryGetValue(uFrom, out double bFrom) && DataToBytes.TryGetValue(uTo, out double bTo))
        {
            double res = val * bFrom / bTo;
            return ($"{res:G6} {uTo.ToUpperInvariant()}", $"Data: {val} {uFrom.ToUpperInvariant()} → {uTo.ToUpperInvariant()}");
        }

        // Time
        if (TimeToSeconds.TryGetValue(uFrom, out double sFrom) && TimeToSeconds.TryGetValue(uTo, out double sTo))
        {
            double res = val * sFrom / sTo;
            return ($"{res:G6} {uTo}", $"Time: {val} {uFrom} → {uTo}");
        }

        // Currency
        if (CurrencyToUsd.TryGetValue(uFrom, out double usdFrom) && CurrencyToUsd.TryGetValue(uTo, out double usdTo))
        {
            double res = val * usdFrom / usdTo;
            return ($"{res:F2} {uTo.ToUpperInvariant()}", $"Currency: {val:F2} {uFrom.ToUpperInvariant()} → {uTo.ToUpperInvariant()}");
        }

        return null;
    }

    private static bool IsTemp(string u) => u is "c" or "celsius" or "f" or "fahrenheit" or "k" or "kelvin";

    private static double ConvertTemp(double val, string from, string to)
    {
        double c = from switch
        {
            "c" or "celsius" => val,
            "f" or "fahrenheit" => (val - 32) * 5.0 / 9.0,
            "k" or "kelvin" => val - 273.15,
            _ => val
        };

        return to switch
        {
            "c" or "celsius" => c,
            "f" or "fahrenheit" => c * 9.0 / 5.0 + 32,
            "k" or "kelvin" => c + 273.15,
            _ => c
        };
    }

    private sealed class Parser
    {
        private readonly string _src;
        private int _pos;

        public Parser(string src) => _src = src;

        public double Parse()
        {
            double res = ParseExpression();
            SkipWhitespace();
            if (_pos < _src.Length) throw new InvalidOperationException("Extra characters");
            return res;
        }

        private double ParseExpression()
        {
            double left = ParseTerm();
            while (true)
            {
                SkipWhitespace();
                if (_pos >= _src.Length) break;
                char op = _src[_pos];
                if (op is '+' or '-')
                {
                    _pos++;
                    double right = ParseTerm();
                    left = op == '+' ? left + right : left - right;
                }
                else break;
            }
            return left;
        }

        private double ParseTerm()
        {
            double left = ParseFactor();
            while (true)
            {
                SkipWhitespace();
                if (_pos >= _src.Length) break;
                char op = _src[_pos];
                if (op is '*' or '/' or '%')
                {
                    _pos++;
                    double right = ParseFactor();
                    if (op == '*') left *= right;
                    else if (op == '/')
                    {
                        if (right == 0) throw new DivideByZeroException();
                        left /= right;
                    }
                    else if (op == '%') left %= right;
                }
                else break;
            }
            return left;
        }

        private double ParseFactor()
        {
            double left = ParsePrimary();
            SkipWhitespace();
            if (_pos < _src.Length && _src[_pos] == '^')
            {
                _pos++;
                double right = ParseFactor(); // Right-associative
                return Math.Pow(left, right);
            }
            return left;
        }

        private double ParsePrimary()
        {
            SkipWhitespace();
            if (_pos >= _src.Length) throw new InvalidOperationException("Unexpected end");

            if (_src[_pos] == '+') { _pos++; return ParsePrimary(); }
            if (_src[_pos] == '-') { _pos++; return -ParsePrimary(); }

            if (_src[_pos] == '(')
            {
                _pos++;
                double res = ParseExpression();
                SkipWhitespace();
                if (_pos < _src.Length && _src[_pos] == ')') _pos++;
                else throw new InvalidOperationException("Missing ')'");
                return res;
            }

            if (char.IsDigit(_src[_pos]) || _src[_pos] == '.')
            {
                int start = _pos;
                while (_pos < _src.Length && (char.IsDigit(_src[_pos]) || _src[_pos] == '.')) _pos++;
                string numStr = _src[start.._pos];
                return double.Parse(numStr, CultureInfo.InvariantCulture);
            }

            if (char.IsLetter(_src[_pos]))
            {
                int start = _pos;
                while (_pos < _src.Length && char.IsLetter(_src[_pos])) _pos++;
                string fn = _src[start.._pos].ToLowerInvariant();
                if (fn == "pi") return Math.PI;
                if (fn == "e") return Math.E;

                SkipWhitespace();
                if (_pos < _src.Length && _src[_pos] == '(')
                {
                    _pos++;
                    double arg = ParseExpression();
                    SkipWhitespace();
                    if (_pos < _src.Length && _src[_pos] == ')') _pos++;
                    return fn switch
                    {
                        "sqrt" => Math.Sqrt(arg),
                        "sin" => Math.Sin(arg),
                        "cos" => Math.Cos(arg),
                        "tan" => Math.Tan(arg),
                        "log" => Math.Log10(arg),
                        "ln" => Math.Log(arg),
                        "abs" => Math.Abs(arg),
                        _ => throw new InvalidOperationException($"Unknown function {fn}")
                    };
                }
            }

            throw new InvalidOperationException($"Invalid token at {_pos}");
        }

        private void SkipWhitespace()
        {
            while (_pos < _src.Length && char.IsWhiteSpace(_src[_pos])) _pos++;
        }
    }
}
