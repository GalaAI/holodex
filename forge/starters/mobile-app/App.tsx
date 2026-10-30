// 4GE mobile starter. Adapted from Expo's official expo-template-blank-typescript
// (SDK 58, 0BSD): same entry (index.ts, registerRootComponent) and config,
// plus one real screen so the scaffold is something you can use and grow.
import { StatusBar } from 'expo-status-bar';
import { useState } from 'react';
import { FlatList, Pressable, StyleSheet, Text, TextInput, View } from 'react-native';
import brand from './src/brand.json';
import { addItem, removeItem, type Item } from './src/items';

export default function App() {
  const [items, setItems] = useState<Item[]>([]);
  const [draft, setDraft] = useState('');

  const add = () => {
    setItems((current) => addItem(current, draft));
    setDraft('');
  };

  return (
    <View style={styles.screen}>
      <Text style={styles.eyebrow}>{`// ${brand.name.toUpperCase()}`}</Text>
      <Text style={styles.title}>{brand.tagline}</Text>
      <View style={styles.row}>
        <TextInput
          accessibilityLabel="New item"
          placeholder="Add an item"
          placeholderTextColor={colors.muted}
          style={styles.input}
          value={draft}
          onChangeText={setDraft}
          onSubmitEditing={add}
          returnKeyType="done"
        />
        <Pressable accessibilityRole="button" onPress={add} style={styles.button}>
          <Text style={styles.buttonText}>Add</Text>
        </Pressable>
      </View>
      <FlatList
        data={items}
        keyExtractor={(item) => item.id}
        ListEmptyComponent={<Text style={styles.empty}>Nothing here yet. Add your first item above.</Text>}
        renderItem={({ item }) => (
          <Pressable
            accessibilityHint="Removes this item"
            onLongPress={() => setItems((current) => removeItem(current, item.id))}
            style={styles.item}
          >
            <Text style={styles.itemText}>{item.label}</Text>
          </Pressable>
        )}
      />
      <StatusBar style="light" />
    </View>
  );
}

const colors = {
  background: '#0B0D0E',
  surface: '#15191B',
  text: '#E8E2D0',
  muted: '#8A877C',
  primary: '#79C4B7',
};

const styles = StyleSheet.create({
  screen: { flex: 1, backgroundColor: colors.background, paddingTop: 72, paddingHorizontal: 20 },
  eyebrow: { color: colors.muted, fontFamily: 'monospace', letterSpacing: 2, fontSize: 12 },
  title: { color: colors.text, fontSize: 26, fontWeight: '600', marginTop: 8, marginBottom: 24 },
  row: { flexDirection: 'row', gap: 8, marginBottom: 16 },
  input: {
    flex: 1,
    backgroundColor: colors.surface,
    color: colors.text,
    borderRadius: 10,
    paddingHorizontal: 14,
    paddingVertical: 12,
  },
  button: { backgroundColor: colors.primary, borderRadius: 10, paddingHorizontal: 18, justifyContent: 'center' },
  buttonText: { color: colors.background, fontWeight: '700' },
  empty: { color: colors.muted, marginTop: 24 },
  item: { backgroundColor: colors.surface, borderRadius: 10, padding: 14, marginBottom: 8 },
  itemText: { color: colors.text, fontSize: 16 },
});
