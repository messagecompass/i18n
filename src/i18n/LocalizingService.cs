﻿using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Web;
using System.Web.Caching;
using System.Web.Hosting;

namespace i18n
{
    /// <summary>
    /// A service for retrieving localized text from PO resource files
    /// </summary>
    public class LocalizingService : ILocalizingService
    {
        private static readonly object _sync = new object();

        /// <summary>
        /// Returns the best matching language for this application's resources, based the provided languages
        /// </summary>
        /// <param name="languages">A sorted list of language preferences</param>
        public virtual string GetBestAvailableLanguageFrom(string[] languages)
        {
            foreach (var language in languages.Where(language => !string.IsNullOrWhiteSpace(language)))
            {
                var culture = GetCultureInfoFromLanguage(language);
                
                // en-US
                var result = GetLanguageIfAvailable(culture.IetfLanguageTag);
                if(result != null)
                {
                    return result;
                }

                //NOTE: disabled this as in any case cultures derivant from en are ignored,
                // it could be ok to use the key if the language is just en, but with this code
                // en-US en-AU en-CA are all ignored tho obviousely they could be different from 
                // the default en variable
                // next to that "en" cannot be translated using PO files which could be useful
                // Save cycles processing beyond the default; this one is guaranteed
                /*
                if (culture.TwoLetterISOLanguageName.Equals(I18N.DefaultTwoLetterISOLanguageName, StringComparison.OrdinalIgnoreCase))
                {
                    return I18N.DefaultTwoLetterISOLanguageName;
                }*/

                // Don't process the same culture code again
                if (culture.IetfLanguageTag.Equals(culture.TwoLetterISOLanguageName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // en
                result = GetLanguageIfAvailable(culture.TwoLetterISOLanguageName);
                if (result != null)
                {
                    return result;
                }
            }

            return I18N.DefaultTwoLetterISOLanguageName;
        }

        private static string GetLanguageIfAvailable(string culture)
        {
            culture = culture.ToLowerInvariant();

            var cacheKey = string.Format("po:{0}", culture);

            lock (_sync)
            {
                if (HttpRuntime.Cache[cacheKey] != null)
                {
                    // This language is already available
                    return ((List<I18NMessage>)HttpRuntime.Cache[cacheKey]).Count > 0 ? culture : null;
                }
            }

            if (LoadMessages(culture) && ((List<I18NMessage>)HttpRuntime.Cache[cacheKey]).Count > 0)
            {
                return culture;
            }
            
            // Avoid shredding the disk looking for non-existing files
            CreateEmptyMessages(culture);
            return null;
        }

        /// <summary>
        /// Returns localized text for a given default language key, or the default itself,
        /// based on the provided languages and application resources
        /// </summary>
        /// <param name="key">The default language key to search for</param>
        /// <param name="languages">A sorted list of language preferences</param>
        /// <returns></returns>
        public virtual string GetText(string key, string[] languages)
        {
            // Prefer 'en-US', then 'en', before moving to next language choice
            foreach (var language in languages.Where(language => !string.IsNullOrWhiteSpace(language)))
            {
                var culture = GetCultureInfoFromLanguage(language);

                // en-US
                var regional = TryGetTextFor(culture.IetfLanguageTag, key);

                //NOTE: disabled this as in any case cultures derivant from en are ignored,
                // it could be ok to use the key if the language is just en, but with this code
                // en-US en-AU en-CA are all ignored tho obviousely they could be different from 
                // the default en variable
                // next to that "en" cannot be translated using PO files which could be useful
                // Save cycles processing beyond the default; just return the original key
                /*
                if (culture.TwoLetterISOLanguageName.Equals(I18N.DefaultTwoLetterISOLanguageName, StringComparison.OrdinalIgnoreCase))
                {
                    return key;
                }*/

                // en (and regional was defined)
                if(!culture.IetfLanguageTag.Equals(culture.TwoLetterISOLanguageName, StringComparison.OrdinalIgnoreCase) && regional == key)
                {
                    var global = TryGetTextFor(culture.TwoLetterISOLanguageName, key);
                    if(global != key)
                    {
                        return global;
                    }
                    continue;
                }

                if(regional != key)
                {
                    return regional;
                }
            }

            return key;
        }

        private static string TryGetTextFor(string culture, string key)
        {
            lock (_sync)
            {
                if (HttpRuntime.Cache[string.Format("po:{0}", culture)] != null)
                {
                    // This culture is already processed and in memory
                    return GetTextOrDefault(culture, key);
                }
            }

            if(LoadMessages(culture))
            {
                return GetTextOrDefault(culture, key);    
            }
            
            // Avoid shredding the disk looking for non-existing files
            CreateEmptyMessages(culture);

            return key;
        }

        private static void CreateEmptyMessages(string culture)
        {
            lock (_sync)
            {
                string directory;
                string path;
                GetDirectoryAndPath(culture, out directory, out path);

                if (!Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                using(var fs = File.CreateText(path))
                {
                    fs.Flush();
                }

                // If the file changes we want to be able to rebuild the index without recompiling
                HttpRuntime.Cache.Insert(string.Format("po:{0}", culture), new List<I18NMessage>(0), new CacheDependency(path));
            }
        }

        private static bool LoadMessages(string culture)
        {
            string directory;
            string path;
            GetDirectoryAndPath(culture, out directory, out path);

            if (!File.Exists(path))
            {
                return false;
            }

            LoadFromDiskAndCache(culture, path);
            return true;
        }

        private static void GetDirectoryAndPath(string culture, out string directory, out string path)
        {
            directory = string.Format("{0}/locale/{1}", HostingEnvironment.ApplicationPhysicalPath, culture);
            path = Path.Combine(directory, "messages.po");
        }

        private static void LoadFromDiskAndCache(string culture, string path)
        {
            //If the msgstr is 1 word length, e.g. msgstr \"a\", it does not worked
            var quoted = new Regex("(?:\"(?:[^\"]+.)*\")", RegexOptions.Compiled);

            lock (_sync)
            {
                using (var fs = File.OpenText(path))
                {
                    // http://www.gnu.org/s/hello/manual/gettext/PO-Files.html

                    var messages = new List<I18NMessage>(0);
                    string line;
                    while ((line = fs.ReadLine()) != null)
                    {
                        var message = new I18NMessage();
                        var sb = new StringBuilder();

                        if (line.StartsWith("#"))
                        {
                            sb.Append(CleanCommentLine(line));
                            while((line = fs.ReadLine()) != null && line.StartsWith("#"))
                            {
                                sb.Append(CleanCommentLine(line));
                            }
                            message.Comment = sb.ToString();

                            sb.Clear();
                            ParseBody(fs, line, sb, message, quoted);
                            messages.Add(message);
                        }
                        else if (line.StartsWith("msgid"))
                        {
                            ParseBody(fs, line, sb, message, quoted);
                        }
                    }

                    lock (_sync)
                    {
                        // If the file changes we want to be able to rebuild the index without recompiling
                        HttpRuntime.Cache.Insert(string.Format("po:{0}", culture), messages, new CacheDependency(path));
                    }
                }
            }
        }

        private static void ParseBody(TextReader fs, string line, StringBuilder sb, I18NMessage message, Regex quoted)
        {
            if(!string.IsNullOrEmpty(line))
            {
                if(line.StartsWith("msgid"))
                {
                    var msgid = quoted.Match(line).Value;
                    sb.Append(msgid.Substring(1, msgid.Length - 2));
                    
                    while ((line = fs.ReadLine()) != null && !line.StartsWith("msgstr") && !string.IsNullOrWhiteSpace(msgid = quoted.Match(line).Value))
                    {
                        sb.Append(msgid.Substring(1, msgid.Length - 2));
                    }

                    message.MsgId = sb.ToString();
                }

                sb.Clear();
                if(!string.IsNullOrEmpty(line) && line.StartsWith("msgstr"))
                {
                    //var msgstr = quoted.Match(line).Value;
                    //sb.Append(msgstr.Substring(1, msgstr.Length - 2));
                    int firstIndex = line.IndexOf('\"');
                    int lastIndex = line.LastIndexOf('\"');
                    var msgstr = line.Substring(firstIndex+1, lastIndex-firstIndex-1);
                    sb.Append(msgstr);
                    while ((line = fs.ReadLine()) != null && !string.IsNullOrEmpty(msgstr = quoted.Match(line).Value))
                    {
                        sb.Append(msgstr.Substring(1, msgstr.Length - 2));
                    }

                    message.MsgStr = sb.ToString();
                }
            }
        }

        private static string CleanCommentLine(string line)
        {
            return line.Replace("# ", "").Replace("#. ", "").Replace("#: ", "").Replace("#, ", "").Replace("#| ", "");
        }

        private static string GetTextOrDefault(string culture, string key)
        {
            lock (_sync)
            {
                var messages = ((List<I18NMessage>) HttpRuntime.Cache[string.Format("po:{0}", culture)]).Where(m => m.MsgId != null);

                if (messages.Count() == 0)
                {
                    return key;
                }

                var matched = messages.SingleOrDefault(m => m.MsgId.Equals(key));

                if (matched == null)
                {
                    return key;
                }

                return string.IsNullOrWhiteSpace(matched.MsgStr) ? key : matched.MsgStr;
            }
        }

        private static CultureInfo GetCultureInfoFromLanguage(string language)
        {
            var semiColonIndex = language.IndexOf(';');
            return semiColonIndex > -1
                       ? new CultureInfo(language.Substring(0, semiColonIndex), true)
                       : new CultureInfo(language, true);
        }
    }
}

       