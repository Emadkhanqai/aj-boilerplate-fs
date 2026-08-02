import { definePreset } from '@primeuix/styled';
import Aura from '@primeuix/themes/aura';

/**
 * The PrimeNG theme preset. Base: PrimeNG's Aura preset, whose semantic-token shape maps most
 * directly onto a single-primary-colour brand.
 *
 * This is the ONLY file that defines the component palette. It must stay in step with
 * `src/design/tokens.css` — the `500` slot is the same value as `--brand`, the `800` slot the
 * same as `--brand-deep`. The intermediate steps are conventional interpolations so PrimeNG's
 * hover/active/focus states (which read those slots) still look intentional.
 *
 * REPLACE these values with your brand's, in both places, and record the choice in `DESIGN.md`.
 * Never redefine colours in a feature library's styles.
 */
export const appPreset = definePreset(Aura, {
  semantic: {
    primary: {
      50: '#eef4f0',
      100: '#d9e7de',
      200: '#b3cfbe',
      300: '#8cb69d',
      400: '#589c76',
      500: '#1f5c3d', // --moss (tokens.css)
      600: '#1c5337',
      700: '#184529',
      800: '#143f29', // --moss-deep (tokens.css)
      900: '#0f2f1e',
      950: '#0a2015',
    },
  },
  components: {
    button: {
      colorScheme: {
        light: {
          root: {
            primary: {
              background: '{primary.500}',
              hoverBackground: '{primary.700}',
              activeBackground: '{primary.800}',
            },
          },
        },
      },
    },
  },
});
