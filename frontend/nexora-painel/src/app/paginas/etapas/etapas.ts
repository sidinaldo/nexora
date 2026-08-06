import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { EtapasServico } from '../../nucleo/servicos/etapas.servico';
import { ToastServico } from '../../nucleo/toast/toast.servico';
import { EtapaConfigDto } from '../../nucleo/modelos';

/** CONFIGURAÇÃO DO FUNIL.
 *
 *  Até aqui as cinco etapas eram semeadas no cadastro e nunca mais mudavam — o que serve à
 *  primeira empresa e a mais nenhuma.
 *
 *  ===================== POR QUE SETA E NÃO ARRASTAR =====================
 *  O quadro do funil já tem arrastar-e-soltar para os cards, e ele nunca foi exercitado em
 *  navegador. Repetir a mesma mecânica aqui somaria risco a uma tela de configuração, onde errar
 *  a posição da coluna é bem mais caro que errar a posição de um card.
 *
 *  Setas também funcionam com teclado, que arrastar não faz sem trabalho extra.
 *  ====================================================================== */
@Component({
  selector: 'app-etapas',
  imports: [FormsModule],
  templateUrl: './etapas.html',
  styleUrl: './etapas.css'
})
export class Etapas implements OnInit {
  private servico = inject(EtapasServico);
  private toast = inject(ToastServico);

  /** Espelha `ServicoEtapas.MaximoEtapas`. Duplicado de propósito: a tela precisa esconder o
   *  formulário ANTES de o dono digitar um nome e levar 400. O servidor continua sendo quem
   *  decide — aqui é só cortesia. */
  readonly maximo = 12;

  lista = signal<EtapaConfigDto[]>([]);
  carregando = signal(true);
  erro = signal('');
  salvando = signal(false);

  fNome = signal('');
  fCor = signal('#5C8F6E');
  erroNovo = signal('');

  editandoId = signal<number | null>(null);
  eNome = signal('');
  eCor = signal('');

  /** A etapa cuja remoção está sendo confirmada, e para onde os contatos vão. */
  removendo = signal<EtapaConfigDto | null>(null);
  destino = signal<number | null>(null);

  cheio = computed(() => this.lista().length >= this.maximo);

  /** Onde o lead novo cai: a de menor ordem. A tela mostra isso porque é a consequência menos
   *  óbvia de reordenar — mover uma coluna para o topo muda onde todo lead futuro nasce.
   *
   *  O tipo é explícito: sem ele o TS infere `EtapaConfigDto` (índice de array não vem com
   *  `undefined` neste tsconfig), o `?? null` vira código morto e o template acusa NG8107 no
   *  `primeira()?.nome` — que é justamente a leitura correta, porque a lista pode estar vazia. */
  primeira = computed<EtapaConfigDto | null>(() => this.lista()[0] ?? null);

  /** Destinos possíveis ao apagar: todas menos a que está sendo apagada. */
  destinos = computed(() => {
    const alvo = this.removendo();
    return alvo === null ? [] : this.lista().filter(e => e.id !== alvo.id);
  });

  /** Só se pode apagar se sobrar ao menos uma NÃO-ganho — é a invariante que o banco não
   *  garante: o lead novo entra na menor ordem, e se só sobrar a de ganho todo lead nasce ganho. */
  podeApagar(e: EtapaConfigDto): boolean {
    if (e.eGanho) return false;
    return this.lista().filter(x => !x.eGanho && x.id !== e.id).length >= 1;
  }

  ngOnInit() { this.carregar(); }

  carregar() {
    this.carregando.set(true);
    this.servico.listar().subscribe({
      next: l => { this.lista.set(l); this.carregando.set(false); this.erro.set(''); },
      error: () => {
        this.erro.set('Não foi possível carregar as etapas.');
        this.carregando.set(false);
      }
    });
  }

  // ---------------------------------------------------------------- criar
  criar() {
    const nome = this.fNome().trim();
    if (nome.length < 2) { this.erroNovo.set('Dê um nome à etapa.'); return; }

    this.salvando.set(true);
    this.erroNovo.set('');
    this.servico.criar(nome, this.fCor()).subscribe({
      next: () => {
        this.salvando.set(false);
        this.fNome.set('');
        this.toast.sucesso(`"${nome}" entrou no fim do funil. Use as setas para posicionar.`);
        this.carregar();
      },
      error: e => {
        this.salvando.set(false);
        this.erroNovo.set(e.error?.erro ?? 'Não foi possível criar.');
      }
    });
  }

  // ---------------------------------------------------------------- editar
  editar(e: EtapaConfigDto) {
    this.editandoId.set(e.id);
    this.eNome.set(e.nome);
    this.eCor.set(e.cor);
  }

  cancelarEdicao() { this.editandoId.set(null); }

  salvarEdicao(e: EtapaConfigDto) {
    this.servico.atualizar(e.id, this.eNome().trim(), this.eCor()).subscribe({
      next: () => {
        this.editandoId.set(null);
        this.toast.sucesso('Etapa atualizada.');
        this.carregar();
      },
      error: err => this.toast.erro(err.error?.erro ?? 'Não foi possível salvar.')
    });
  }

  // ---------------------------------------------------------------- ordem
  mover(indice: number, passo: -1 | 1) {
    const atual = this.lista();
    const alvo = indice + passo;
    if (alvo < 0 || alvo >= atual.length) return;

    const nova = [...atual];
    [nova[indice], nova[alvo]] = [nova[alvo], nova[indice]];

    // Pinta na hora e manda a ordem inteira. Se a API recusar, `carregar()` traz a verdade de
    // volta — nenhum estado local sobrevive a um erro.
    this.lista.set(nova);

    this.servico.reordenar(nova.map(e => e.id)).subscribe({
      next: () => this.carregar(),
      error: err => {
        this.toast.erro(err.error?.erro ?? 'Não foi possível reordenar.');
        this.carregar();
      }
    });
  }

  // ---------------------------------------------------------------- ganho
  definirGanho(e: EtapaConfigDto) {
    if (e.eGanho) return;

    if (!confirm(
      `Marcar "${e.nome}" como a etapa de ganho?\n\n` +
      `É ela que conta como venda no dashboard, e a taxa de conversão passa a ser medida por ` +
      `ela — inclusive para o histórico já registrado.\n\n` +
      `"${this.lista().find(x => x.eGanho)?.nome}" deixa de ser.`)) return;

    this.servico.definirGanho(e.id).subscribe({
      next: () => { this.toast.sucesso(`"${e.nome}" agora é a etapa de ganho.`); this.carregar(); },
      error: err => this.toast.erro(err.error?.erro ?? 'Não foi possível alterar.')
    });
  }

  // ---------------------------------------------------------------- remover
  pedirRemocao(e: EtapaConfigDto) {
    this.removendo.set(e);
    // Pré-seleciona a etapa anterior: é para onde o contato "volta" naturalmente quando a
    // coluna em que ele estava deixa de existir.
    const antes = this.lista().filter(x => x.id !== e.id && x.ordem < e.ordem);
    this.destino.set((antes.length > 0 ? antes[antes.length - 1] : this.destinos()[0])?.id ?? null);
  }

  cancelarRemocao() { this.removendo.set(null); }

  confirmarRemocao() {
    const alvo = this.removendo();
    if (alvo === null) return;

    // Sem contatos, destino não é necessário e nem é mandado.
    const destino = alvo.contatos > 0 ? this.destino() : null;
    if (alvo.contatos > 0 && destino === null) {
      this.toast.erro('Escolha para qual etapa os contatos vão.');
      return;
    }

    this.servico.remover(alvo.id, destino).subscribe({
      next: () => {
        this.removendo.set(null);
        this.toast.sucesso(alvo.contatos > 0
          ? `"${alvo.nome}" apagada. Os contatos foram movidos.`
          : `"${alvo.nome}" apagada.`);
        this.carregar();
      },
      error: err => this.toast.erro(err.error?.erro ?? 'Não foi possível apagar.')
    });
  }

  nomeDe(id: number | null): string {
    return this.lista().find(e => e.id === id)?.nome ?? '';
  }
}
