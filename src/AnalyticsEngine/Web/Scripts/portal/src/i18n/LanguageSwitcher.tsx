import {
  Menu,
  MenuItemRadio,
  MenuList,
  MenuPopover,
  MenuTrigger,
  ToolbarButton,
  Tooltip,
  makeStyles,
  tokens,
} from '@fluentui/react-components';
import { LocalLanguage20Regular } from '@fluentui/react-icons';

import { useI18n } from './I18nProvider';
import { LANGUAGES, isLanguage, languageDefinition } from './languages';

const useStyles = makeStyles({
  trigger: {
    color: tokens.colorNeutralForegroundOnBrand,
  },
});

/**
 * The language picker in the brand bar.
 *
 * Sits in the header rather than on a settings page on purpose: someone who has landed on a
 * portal in a language they do not read cannot navigate to a settings page to fix it. It has to be
 * reachable from wherever they are, by an icon they recognise without reading anything.
 *
 * Each option is named in its own language ("Espanol", not "Spanish") for the same reason - the
 * list has to be legible to someone who cannot read the language the page is currently in.
 */
export default function LanguageSwitcher() {
  const styles = useStyles();
  const { language, setLanguage, t } = useI18n();
  const current = languageDefinition(language);

  return (
    <Menu
      checkedValues={{ language: [language] }}
      onCheckedValueChange={(_event, data) => {
        const next = data.checkedItems[0];
        if (isLanguage(next)) setLanguage(next);
      }}
    >
      <MenuTrigger disableButtonEnhancement>
        <Tooltip content={t('app.language.choose')} relationship="label">
          <ToolbarButton
            className={styles.trigger}
            appearance="transparent"
            icon={<LocalLanguage20Regular />}
            aria-label={t('app.language.current', { language: current.nativeName })}
          >
            {current.nativeName}
          </ToolbarButton>
        </Tooltip>
      </MenuTrigger>
      <MenuPopover>
        <MenuList>
          {LANGUAGES.map((definition) => (
            <MenuItemRadio key={definition.id} name="language" value={definition.id}>
              {definition.nativeName}
            </MenuItemRadio>
          ))}
        </MenuList>
      </MenuPopover>
    </Menu>
  );
}
