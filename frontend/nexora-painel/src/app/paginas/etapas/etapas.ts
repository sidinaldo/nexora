import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute } from '@angular/router';
import { EtapasServico } from '../../nucleo/servicos/etapas.servico';
import { PipelinesServico } from '../../nucleo/servicos/pipelines.servico';
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
 *  ======================================================================
 *
 *  ===================== ⚠️ ESTA TELA EDITA UM FUNIL, NÃO "O" FUNIL =====================
 *  A rota é `crm/:pipeline/etapas`, e até este bloco o componente NUNCA LIA esse parâmetro. As
 *  três operações que dependem do funil — listar, criar e reordenar — iam sem `?pipeline=`, e o
 *  servidor caía na pipeline PADRÃO.
 *
 *  O resultado: quem abria "Pós-venda" via e editava as etapas de "Vendas". Renomear uma etapa
 *  ali mudava o nome no outro funil, e o cabeçalho genérico ("Seu funil") não dava nenhuma
 *  pista de que a tela estava em outro lugar.
 *
 *  Por isso o nome do funil agora aparece no título: o erro de contexto tem de ser VISÍVEL.
 *  ==================================================================================== */
@Component({
  selector: 'app-etapas',
  imports: [FormsModule],
  templateUrl: './etapas.html',
  styleUrl: './etapas.css'
})
export class Etapas implements OnInit {
  private servico = inject(EtapasServico);
  private pipelines = inject(PipelinesServico);
  private rota = inject(ActivatedRoute);
  private toast = inject(ToastServico);

  /** O funil que esta tela configura. Vem da rota e vai em TODA operação que depende dele. */
  pipeline = signal<number | null>(null);

  /** O nome, só para o cabeçalho.
   *
   *  Sai do sinal compartilhado de `PipelinesServico`, que o shell carrega no boot — nada de
   *  requisição própria só para escrever um título. Vazio enquanto a lista não chegou; o título
   *  tolera. */
  nomeDoFunil = computed(() => {
    const id = this.pipeline();
    return id === null ? '' : (this.pipelines.lista().find(p => p.id === id)?.nome ?? '');
  });

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

  ngOnInit() {
    // ===================== ASSINA `paramMap`, NÃO LÊ `snapshot` =====================
    // Ir de `/crm/3/etapas` para `/crm/4/etapas` REUTILIZA este componente — o Angular não o
    // destrói, só troca o parâmetro. Um `snapshot` lido uma vez desenharia o primeiro funil e
    // nunca mais mudaria: trocar de funil no menu não faria nada, sem erro nenhum.
    //
    // O quadro já registra este mesmo cuidado e diz "não vai haver um terceiro caso". Esta tela
    // era o terceiro, e pior: ela não lia o parâmetro de forma alguma.
    // ===============================================================================
    this.rota.paramMap.subscribe(p => {
      const id = Number(p.get('pipeline') ?? 0);

      if (!(id > 0)) {
        // Sem funil não há o que configurar. Cair na padrão aqui seria repetir o defeito.
        this.pipeline.set(null);
        this.erro.set('Funil não encontrado. Escolha um no menu.');
        this.carregando.set(false);
        return;
      }

      this.pipeline.set(id);
      this.limparEstadoDaTela();
      this.carregar();
    });
  }

  /** ⚠️ O QUE PRECISA MORRER AO TROCAR DE FUNIL. Como o componente é reutilizado, tudo isto
   *  sobreviveria à troca e passaria a apontar para etapas que não existem mais — `editandoId`
   *  gravaria o nome digitado numa etapa do funil anterior. */
  private limparEstadoDaTela() {
    this.editandoId.set(null);
    this.removendo.set(null);
    this.destino.set(null);
    this.fNome.set('');
    this.erroNovo.set('');
    this.erro.set('');
  }

  carregar() {
    this.carregando.set(true);
    this.servico.listar(this.pipeline()).subscribe({
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
    this.servico.criar(nome, this.fCor(), this.pipeline()).subscribe({
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

    this.servico.reordenar(nova.map(e => e.id), this.pipeline()).subscribe({
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
