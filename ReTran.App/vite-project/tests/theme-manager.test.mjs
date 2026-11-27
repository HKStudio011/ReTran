import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import test from 'node:test';

const modulePath = new URL('../../wwwroot/build/assets/main.js', import.meta.url);

test('startup restores theme without applying a persisted accent override', async () => {
    const source = await readFile(modulePath, 'utf8');

    for (const [theme, accent] of [['dark', 'jade'], ['light', 'coral']]) {
        const attributes = new Map();
        const stored = new Map([
            ['retran-theme', theme],
            ['retran-accent', accent],
            ['retran-color', 'default'],
        ]);

        globalThis.document = {
            documentElement: {
                setAttribute: (name, value) => attributes.set(name, value),
                removeAttribute: name => attributes.delete(name),
                getAttribute: name => attributes.get(name) ?? null,
            },
        };
        globalThis.window = {
            matchMedia: () => ({ addEventListener: () => {} }),
        };
        globalThis.localStorage = {
            getItem: key => stored.get(key) ?? null,
            setItem: (key, value) => stored.set(key, value),
        };

        const moduleSource = `${source}\n// theme test: ${theme}-${accent}`;
        const moduleUrl = `data:text/javascript;base64,${Buffer.from(moduleSource).toString('base64')}`;
        await import(moduleUrl);

        assert.equal(attributes.get('data-theme'), theme);
        assert.equal(attributes.has('data-accent'), false,
            `${accent} from old localStorage must not override the single brand accent`);
    }
});
