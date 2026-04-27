using ContextKing.Core;
using ContextKing.Core.Ast;

namespace ContextKing.Cli.Commands;

internal static class GetUsingsCommand
{
    internal static Task<int> RunAsync(string[] args)
    {
        var reader = new ArgReader(args);
        if (reader.IsEmpty || reader.IsHelp)
        {
            PrintHelp();
            return Task.FromResult(reader.IsEmpty ? 1 : 0);
        }

        var positional = reader.RemainingPositionals();
        if (positional.Count < 1)
        {
            Console.Error.WriteLine("[ck get-usings] Error: file path is required.");
            PrintHelp();
            return Task.FromResult(1);
        }

        var filePath = positional[0];
        if (!File.Exists(filePath))
        {
            Console.Error.WriteLine($"[ck get-usings] Error: file not found: '{filePath}'");
            return Task.FromResult(1);
        }

        if (!SupportedLanguages.IsSupported(filePath))
        {
            Console.Error.WriteLine($"[ck get-usings] Error: unsupported file type: '{filePath}'. Supported: .cs, .ts, .tsx");
            return Task.FromResult(1);
        }

        try
        {
            var directives = UsingsExtractor.Extract(filePath);

            if (directives.Count == 0)
            {
                Console.Error.WriteLine($"[ck get-usings] No using/import directives found in '{filePath}'.");
                return Task.FromResult(0);
            }

            foreach (var d in directives)
                Console.WriteLine(d);

            return Task.FromResult(0);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ck get-usings] Error: {ex.Message}");
            return Task.FromResult(1);
        }
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
            ck get-usings — list all using directives (C#) or import statements (TypeScript) in a file

            Usage:
              ck get-usings <file>

            Supports C# (.cs), TypeScript (.ts), and TSX (.tsx) files.

            Output (stdout):
              One using/import line per output line, in source order.

            Examples (C#):
              using System;
              using System.Collections.Generic;
              using Mews.Accounting.Core.Payments;

            Examples (TypeScript):
              import React from 'react';
              import { useState, useEffect } from 'react';
              import type { PaymentGateway } from '../types';

            Notes:
              - Always reads from disk; reflects uncommitted edits immediately.
              - Useful when editing a file and needing to add a new import without reading the full file.
              - For C#, captures both top-level and namespace-scoped using directives.
            """);
    }
}
