using System.Globalization;

namespace Glide.Core;

/// <summary>
/// Simple recursive-descent calculator for basic arithmetic operations.
/// Supports +, -, *, /, parenthesis, and integer/double numbers.
/// </summary>
public static class Calculator
{
    public static bool TryEvaluate(ReadOnlySpan<char> expression, out double result)
    {
        try
        {
            var parser = new Parser(expression);
            result = parser.Evaluate();
            return true;
        }
        catch (FormatException)
        {
            result = 0;
            return false;
        }
        catch (DivideByZeroException)
        {
            result = double.NaN;
            return true; // Report NaN as a valid result for division by zero
        }
    }

    private ref struct Parser
    {
        private readonly ReadOnlySpan<char> _expression;
        private int _position;

        public Parser(ReadOnlySpan<char> expression)
        {
            _expression = expression;
            _position = 0;
        }

        public double Evaluate()
        {
            double result = ParseExpression();
            SkipWhitespace();
            if (_position < _expression.Length)
            {
                throw new FormatException("Unexpected characters at end of expression.");
            }
            return result;
        }

        private double ParseExpression()
        {
            double result = ParseTerm();

            while (true)
            {
                SkipWhitespace();
                if (_position < _expression.Length && _expression[_position] == '+')
                {
                    _position++;
                    result += ParseTerm();
                }
                else if (_position < _expression.Length && _expression[_position] == '-')
                {
                    _position++;
                    result -= ParseTerm();
                }
                else
                {
                    return result;
                }
            }
        }

        private double ParseTerm()
        {
            double result = ParseFactor();

            while (true)
            {
                SkipWhitespace();
                if (_position < _expression.Length && _expression[_position] == '*')
                {
                    _position++;
                    result *= ParseFactor();
                }
                else if (_position < _expression.Length && _expression[_position] == '/')
                {
                    _position++;
                    double divisor = ParseFactor();
                    if (divisor == 0)
                    {
                        throw new DivideByZeroException();
                    }
                    result /= divisor;
                }
                else
                {
                    return result;
                }
            }
        }

        private double ParseFactor()
        {
            SkipWhitespace();
            if (_position >= _expression.Length)
            {
                throw new FormatException("Unexpected end of expression.");
            }

            if (_expression[_position] == '(')
            {
                _position++;
                double result = ParseExpression();
                SkipWhitespace();
                if (_position >= _expression.Length || _expression[_position] != ')')
                {
                    throw new FormatException("Missing closing parenthesis.");
                }
                _position++;
                return result;
            }
            else if (_expression[_position] == '-')
            {
                _position++;
                return -ParseFactor();
            }
            else
            {
                return ParseNumber();
            }
        }

        private double ParseNumber()
        {
            int start = _position;
            while (_position < _expression.Length && (char.IsDigit(_expression[_position]) || _expression[_position] == '.'))
            {
                _position++;
            }

            if (_position == start)
            {
                throw new FormatException($"Expected number at position {start}.");
            }

            ReadOnlySpan<char> numberSpan = _expression.Slice(start, _position - start);
            if (double.TryParse(numberSpan, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
            {
                return value;
            }
            throw new FormatException($"Invalid number format: {numberSpan.ToString()} at position {start}.");
        }

        private void SkipWhitespace()
        {
            while (_position < _expression.Length && char.IsWhiteSpace(_expression[_position]))
            {
                _position++;
            }
        }
    }
}
