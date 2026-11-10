import { Button } from 'antd';
import { createContext, useCallback, useContext, useMemo, useState } from 'react';
import type { ReactNode } from 'react';

// The console's two languages (FIX-WEB-16), written as a pair at the call site —
// t('Sign in', '登录') — so every screen shows both words side by side in review and
// there is no key table to drift or a dictionary entry to forget. English is the
// default (the tests read the default face); the choice rides localStorage and
// survives reloads. The toggle sits in the shell header and on the login card —
// the first face a stranger lands on is exactly where the language choice matters.
export type Language = 'en' | 'zh';

type I18n = {
  lang: Language;
  setLang: (lang: Language) => void;
  t: (en: string, zh: string) => string;
};

const LanguageContext = createContext<I18n | null>(null);

const storageKey = 'chorus-console-language';

function initialLanguage(): Language {
  try {
    return localStorage.getItem(storageKey) === 'zh' ? 'zh' : 'en';
  } catch {
    return 'en';
  }
}

export function LanguageProvider({ children }: { children: ReactNode }) {
  const [lang, setLangState] = useState<Language>(initialLanguage);
  const setLang = useCallback((next: Language) => {
    setLangState(next);
    try {
      localStorage.setItem(storageKey, next);
    } catch {
      // A storage that refuses the write keeps the choice for this session only.
    }
  }, []);
  const t = useCallback((en: string, zh: string) => (lang === 'zh' ? zh : en), [lang]);
  const value = useMemo(() => ({ lang, setLang, t }), [lang, setLang, t]);
  return <LanguageContext.Provider value={value}>{children}</LanguageContext.Provider>;
}

export function useI18n(): I18n {
  const value = useContext(LanguageContext);
  if (value === null) {
    throw new Error('useI18n outside the language provider');
  }
  return value;
}

/** The toggle names the language the click leads to, not the one on screen. */
export function LanguageToggle() {
  const { lang, setLang } = useI18n();
  return (
    <Button
      type="text"
      size="small"
      aria-label="Switch language"
      onClick={() => setLang(lang === 'en' ? 'zh' : 'en')}
    >
      {lang === 'en' ? '中文' : 'EN'}
    </Button>
  );
}
