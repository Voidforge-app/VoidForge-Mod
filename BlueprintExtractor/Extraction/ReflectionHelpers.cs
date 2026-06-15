using System.Reflection;

namespace BlueprintExtractor.Extraction;

/**
 * Shared reflection binding flags used across all blueprint extractors.
 * Centralised here to avoid repeating the same constant in every file that uses reflection.
 */
internal static class ReflectionHelpers {
  /**
   * Matches all instance members regardless of visibility.
   * Most game blueprint fields are non-public, so NonPublic is required.
   */
  internal const BindingFlags AllInstanceFlags =
    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
}