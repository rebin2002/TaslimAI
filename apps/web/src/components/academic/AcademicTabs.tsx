"use client";
/**
 * Issue #1259 (Academic Research Studio) — the four-tab workspace shell.
 *
 * Implements the WAI-ARIA tabs pattern without a component library:
 * - a `tablist` with one `tab` per panel and a roving tabindex, so a keyboard
 *   user reaches the strip with a single stop and moves it with the arrows;
 * - selection follows focus, which is the pattern for always-visible panels;
 * - arrow direction is mirrored for RTL locales, which the product supports for
 *   Arabic and Kurdish;
 * - every panel is a real `tabpanel` labelled by its tab and reachable by Tab.
 *
 * Panel bodies come from the caller through `renderPanel`, so this component
 * owns presentation and keyboard behaviour only and performs no fetching.
 */
import { useRef, type KeyboardEvent as ReactKeyboardEvent, type ReactNode } from "react";
import { academicTabs, nextTabFromKey, type AcademicTab } from "./academicStudioState";
import type { AcademicTranslate } from "./academicStrings";
import styles from "./AcademicResearchStudio.module.css";

export type AcademicTabsProps = {
  tab: AcademicTab;
  onSelect: (tab: AcademicTab) => void;
  t: AcademicTranslate;
  labels: Record<AcademicTab, string>;
  /** Optional short status pill, e.g. a source count. */
  counts?: Partial<Record<AcademicTab, string>>;
  renderPanel: (tab: AcademicTab) => ReactNode;
  /** Stable per-instance prefix so two shells on one page never share ids. */
  idPrefix?: string;
};

function isRtl(element: Element | null): boolean {
  const host = element?.closest("[dir]") as HTMLElement | null;
  if (host) return host.getAttribute("dir") === "rtl";
  return typeof document !== "undefined" && document.dir === "rtl";
}

export function AcademicTabs({ tab, onSelect, t, labels, counts = {}, renderPanel, idPrefix = "academic" }: AcademicTabsProps) {
  const refs = useRef<Partial<Record<AcademicTab, HTMLButtonElement | null>>>({});

  function move(next: AcademicTab) {
    onSelect(next);
    refs.current[next]?.focus();
  }

  function handleKeyDown(event: ReactKeyboardEvent<HTMLButtonElement>) {
    const current = event.currentTarget.dataset.tab as AcademicTab;
    const next = nextTabFromKey(current, event.key, { rtl: isRtl(event.currentTarget) });
    if (!next) return;
    event.preventDefault();
    move(next);
  }

  return (
    <div className={styles.tabs}>
      <div className={styles.tablist} role="tablist" aria-label={t("tabs.ariaLabel")}>
        {academicTabs.map((entry) => {
          const selected = entry === tab;
          const count = counts[entry];
          return (
            <button
              key={entry}
              type="button"
              role="tab"
              id={`${idPrefix}-tab-${entry}`}
              data-tab={entry}
              aria-selected={selected}
              aria-controls={`${idPrefix}-panel-${entry}`}
              // Roving tabindex: exactly one tab is in the sequential order.
              tabIndex={selected ? 0 : -1}
              className={`${styles.tab} ${selected ? styles.tabActive : ""}`}
              ref={(node) => {
                refs.current[entry] = node;
              }}
              onKeyDown={handleKeyDown}
              onClick={() => move(entry)}
            >
              <span>{labels[entry]}</span>
              {count ? <span className={styles.tabCount}>{count}</span> : null}
            </button>
          );
        })}
      </div>
      <div
        role="tabpanel"
        id={`${idPrefix}-panel-${tab}`}
        aria-labelledby={`${idPrefix}-tab-${tab}`}
        tabIndex={0}
        className={styles.tabpanel}
      >
        {renderPanel(tab)}
      </div>
    </div>
  );
}
