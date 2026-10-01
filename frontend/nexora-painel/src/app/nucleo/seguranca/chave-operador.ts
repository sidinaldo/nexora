import { Injectable, computed, signal } from '@angular/core';

/** A chave de administração, enquanto a aba estiver aberta.
 *
 *  ===================== POR QUE ELA PRECISA DURAR MAIS DE UMA TELA =====================
 *  A tela de criar empresa pede a chave, usa e esquece — um gesto, um formulário. A área do
 *  operador tem lista, detalhe e catálogo, e o operador vai e volta entre eles o tempo todo. Pedir
 *  a chave a cada navegação transformaria a área em algo que ninguém usa.
 *
 *  Então ela vive AQUI, num signal, pelo tempo da aba. Não é compromisso: é o mesmo tempo de vida
 *  que teria se ficasse num campo de formulário da tela.
 *  =====================================================================================
 *
 *  ⚠️ NADA DISTO VAI PARA `localStorage` NEM `sessionStorage`. Recarregar a página pede a chave de
 *  novo, e isso é o comportamento desejado — não um esquecimento a ser "corrigido" depois. A única
 *  persistência deste painel é a do `AuthServico`, e ela guarda sessão de cliente, não segredo de
 *  operador.
 *
 *  ⚠️ E O QUE ISTO NÃO PROTEGE: extensão de navegador com permissão neste domínio, e a área de
 *  transferência por onde a chave passou ao ser colada. Está registrado em `docs/INF-1.md` como
 *  parte do preço de ter tirado o SSH da rotina. */
@Injectable({ providedIn: 'root' })
export class ChaveOperador {
  private readonly valor = signal('');

  readonly temChave = computed(() => this.valor().length > 0);

  /** Lida só na hora de montar o cabeçalho da requisição. */
  get atual(): string { return this.valor(); }

  definir(chave: string) { this.valor.set((chave ?? '').trim()); }

  /** Sair da área, ou um 401: a chave que não serve não fica guardada esperando a próxima tela
   *  falhar com ela. */
  limpar() { this.valor.set(''); }
}
