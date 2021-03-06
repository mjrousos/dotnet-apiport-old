﻿// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.IO;

namespace Microsoft.Fx.Portability
{
    public static class BreakingChangeParser
    {
        private enum ParseState
        {
            None,
            Scope,
            VersionBroken,
            VersionFixed,
            Details,
            Suggestion,
            AffectedAPIs,
            OriginalBug,
            Notes
        }

        /// <summary>
        /// Parses markdown files into BrekaingChange objects
        /// </summary>
        /// <param name="stream">The markdown to parse</param>
        /// <returns>BreakingChanges parsed from the markdown</returns>
        public static IEnumerable<BreakingChange> FromMarkdown(Stream stream)
        {
            var breakingChanges = new List<BreakingChange>();
            var state = ParseState.None;

            using (var sr = new StreamReader(stream))
            {
                BreakingChange currentBreak = null;
                string currentLine;

                while (null != (currentLine = sr.ReadLine()))
                {
                    currentLine = currentLine.Trim();

                    // New breaking change
                    if (currentLine.StartsWith("## ", StringComparison.Ordinal))
                    {
                        // Save previous breaking change and reset currentBreak
                        if (currentBreak != null)
                        {
                            CleanAndAddBreak(breakingChanges, currentBreak);
                        }
                        currentBreak = new BreakingChange();

                        // Separate ID and title
                        var splitTitle = currentLine.Substring("## ".Length).Split(new[] { ':' }, 2);
                        if (splitTitle.Length == 1)
                        {
                            // Breaking changes are keyed on title, not ID, so if ':' is missing, just take the line as a title.
                            // Note that this will make it impossible to suppress the breaking change, though.
                            currentBreak.Title = splitTitle[0].Trim();
                        }
                        else if (splitTitle.Length == 2)
                        {
                            currentBreak.Id = splitTitle[0].Trim();
                            currentBreak.Title = splitTitle[1].Trim();
                        }

                        // Clear state
                        state = ParseState.None;
                    }
                    else if (currentBreak != null) // Only parse breaking change if we've seeng a breaking change header ("## ...")
                    {
                        // State changes
                        if (currentLine.StartsWith("###", StringComparison.Ordinal))
                        {
                            switch (currentLine.Substring("###".Length).Trim().ToLowerInvariant())
                            {
                                case "scope":
                                    state = ParseState.Scope;
                                    break;
                                case "version introduced":
                                case "version broken":
                                    state = ParseState.VersionBroken;
                                    break;
                                case "version reverted":
                                case "version fixed":
                                    state = ParseState.VersionFixed;
                                    break;
                                case "change description":
                                case "details":
                                    state = ParseState.Details;
                                    break;
                                case "recommended action":
                                case "suggestion":
                                    state = ParseState.Suggestion;
                                    break;
                                case "affected apis":
                                case "applicableapis":
                                    state = ParseState.AffectedAPIs;
                                    break;
                                case "original bug":
                                case "buglink":
                                case "bug":
                                    state = ParseState.OriginalBug;
                                    break;
                                case "notes":
                                    state = ParseState.Notes;
                                    break;
                                default:
                                    ParseNonStateChange(currentBreak, state, currentLine);
                                    break;
                            }
                        }

                        // Bool properties
                        else if (currentLine.StartsWith("- [ ]", StringComparison.Ordinal) || 
                                 currentLine.StartsWith("- [x]", StringComparison.OrdinalIgnoreCase))
                        {
                            bool isChecked = currentLine.StartsWith("- [x]", StringComparison.OrdinalIgnoreCase);
                            switch (currentLine.Substring("- [x]".Length).Trim().ToLowerInvariant())
                            {
                                case "quirked":
                                case "isquirked":
                                    currentBreak.IsQuirked = isChecked;
                                    state = ParseState.None;
                                    break;
                                case "build-time break":
                                case "isbuildtime":
                                    currentBreak.IsBuildTime = isChecked;
                                    state = ParseState.None;
                                    break;
                                case "source analyzer available":
                                case "issourceanalyzeravailable":
                                    currentBreak.IsSourceAnalyzerAvailable = isChecked;
                                    state = ParseState.None;
                                    break;
                                default:
                                    ParseNonStateChange(currentBreak, state, currentLine);
                                    break;
                            }
                        }

                        // More info link
                        else if (currentLine.StartsWith("[More information]", StringComparison.OrdinalIgnoreCase))
                        {
                            currentBreak.Link = currentLine.Substring("[More information]".Length)
                                .Trim(' ', '(', ')', '[', ']', '\t', '\n', '\r')      // Remove markdown link enclosures
                                .Replace("\\(", "(").Replace("\\)", ")");             // Unescape parens in link
                            state = ParseState.None;
                        }

                        // Otherwise, process according to our current state
                        else
                        {
                            ParseNonStateChange(currentBreak, state, currentLine);
                        }
                    }
                }

                // Add the final break from the file
                if (currentBreak != null)
                {
                    CleanAndAddBreak(breakingChanges, currentBreak);
                }
            }

            return breakingChanges;
        }

        private static void ParseNonStateChange(BreakingChange currentBreak, ParseState state, string currentLine)
        {
            switch (state)
            {
                case ParseState.None:
                    return;
                case ParseState.OriginalBug:
                    currentBreak.BugLink = currentLine.Trim();
                    break;
                case ParseState.Scope:
                    BreakingChangeImpact scope;
                    if (Enum.TryParse<BreakingChangeImpact>(currentLine.Trim(), out scope))
                    {
                        currentBreak.ImpactScope = scope;
                    }
                    break;
                case ParseState.VersionBroken:
                    Version verBroken;
                    if (Version.TryParse(currentLine.Trim(), out verBroken))
                    {
                        currentBreak.VersionBroken = verBroken;
                    }
                    break;
                case ParseState.VersionFixed:
                    Version verFixed;
                    if (Version.TryParse(currentLine.Trim(), out verFixed))
                    {
                        currentBreak.VersionFixed = verFixed;
                    }
                    break;
                case ParseState.AffectedAPIs:
                    // Trim md list markers, as well as comment tags (in case the affected APIs section is followed by a comment)
                    string api = currentLine.Trim().TrimStart('*', '-', ' ', '\t', '<', '!', '-');
                    if (string.IsNullOrWhiteSpace(api)) return;
                    if (currentBreak.ApplicableApis == null)
                    {
                        currentBreak.ApplicableApis = new List<string>();
                    }
                    ((List<string>)currentBreak.ApplicableApis).Add(api);
                    break;
                case ParseState.Details:
                    if (currentBreak.Details == null)
                    {
                        currentBreak.Details = currentLine;
                    }
                    else
                    {
                        currentBreak.Details += ("\n" + currentLine);
                    }
                    break;
                case ParseState.Suggestion:
                    if (currentBreak.Suggestion == null)
                    {
                        currentBreak.Suggestion = currentLine;
                    }
                    else
                    {
                        currentBreak.Suggestion += ("\n" + currentLine);
                    }
                    break;
                case ParseState.Notes:
                    // Special-case the fact that 'notes' will often come at the end of a comment section and we don't need the closing --> in the note.
                    if (currentLine.Trim().Equals("-->")) return;
                    if (currentBreak.Notes == null)
                    {
                        currentBreak.Notes = currentLine;
                    }
                    else
                    {
                        currentBreak.Notes += ("\n" + currentLine);
                    }
                    break;
                default:
                    throw new InvalidOperationException("Unhandled breaking change parse state: " + state.ToString());
            }
        }

        private static void CleanAndAddBreak(List<BreakingChange> breakingChanges, BreakingChange currentBreak)
        {
            // Clean up trailing white-space, etc. from long-form text entries
            if (currentBreak.Details != null) currentBreak.Details = currentBreak.Details.Trim();
            if (currentBreak.Suggestion != null) currentBreak.Suggestion = currentBreak.Suggestion.Trim();
            if (currentBreak.Notes != null) currentBreak.Notes = currentBreak.Notes.Trim();

            breakingChanges.Add(currentBreak);
        }
    }
}