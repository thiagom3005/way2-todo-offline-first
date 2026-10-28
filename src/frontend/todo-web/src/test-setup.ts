// IndexedDB em memória para os testes (Node/jsdom não têm IndexedDB nativo).
import 'fake-indexeddb/auto';

// jsdom não implementa rolagem; o formulário de tarefa rola até a tela ao entrar em edição.
Element.prototype.scrollIntoView ??= () => {};
