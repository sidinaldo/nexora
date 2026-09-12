import {
  Component, ElementRef, Injector, OnInit, afterNextRender, computed, inject, signal
} from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Observable } from 'rxjs';
import { EtiquetasServico } from '../../nucleo/servicos/etiquetas.servico';
import { ToastServico } from '../../nucleo/toast/toast.servico';
import { EtiquetaDto, EtiquetaNaLista } from '../../nucleo/modelos';
import { textoSobre } from '../../nucleo/cor';
import { EtiquetaForm, ValorEtiqueta } from './etiqueta-form';

export type OrdemEtiquetas = 'nome' | 'recentes' | 'uso';

/** A partir de quantos contatos apagar deixa de ser um clique e passa a exigir digitar o nome.
 *
 *  ⚠️ NÃO É UM NÚMERO REDONDO À TOA. Abaixo disso, apagar é reversível em minutos: o dono remarca
 *  os contatos. Acima, remarcar cinquenta e um à mão é trabalho de tarde inteira — e aí a
 *  confirmação precisa custar mais que um clique distraído. */
const DIGITAR_NOME_ACIMA_DE = 50;

/** A partir de quantas etiquetas o campo de busca aparece. Abaixo disso a lista inteira cabe na
 *  tela, e um campo de busca seria mobília: dá trabalho de ler e não resolve nada. */
const BUSCA_A_PARTIR_DE = 10;

/** As sugestões do estado vazio.
 *
 *  ⚠️ NÃO É UMA PALETA. São cinco sementes — os rótulos que quase toda empresa acaba criando, com
 *  uma cor cada para o estado vazio não ser cinco chips idênticos. Quem clicar edita a cor no
 *  segundo seguinte, e a escolha de cor continua livre no seletor do sistema. */
const SUGESTOES: ValorEtiqueta[] = [
  { nome: 'Revendedor', cor: '#2E7A56' },
  { nome: 'Urgente', cor: '#B4552F' },
  { nome: 'Inadimplente', cor: '#8A3F3F' },
  { nome: 'VIP', cor: '#A97A22' },
  { nome: 'Pós-venda', cor: '#1D5B3F' }
];

/** O VOCABULÁRIO DE ETIQUETAS.
 *
 *  Rótulos livres que a empresa cola nos cards — "Revendedor", "Urgente", "Inadimplente". Um
 *  vocabulário só: não existe etiqueta de venda e etiqueta de pós-venda, a mesma "Urgente" serve
 *  a qualquer card.
 *
 *  ===================== POR QUE SÓ O DONO CRIA =====================
 *  Mesma regra de Etapas do funil: é configuração, e define como a empresa inteira nomeia as
 *  coisas. APLICAR a etiqueta é trabalho do dia e vai ser de qualquer papel — deixar o vendedor
 *  criar no meio do atendimento faz nascer "Revendedor", "revenda" e "Revendedores" na mesma
 *  semana, e o filtro por etiqueta passa a achar só um pedaço de cada busca.
 *  =================================================================
 *
 *  ===================== BUSCA E ORDEM SÃO EM MEMÓRIA =====================
 *  A API aceita `?busca=` e `?ordem=`, e esta tela não usa nenhum dos dois. O teto de 60 é
 *  garantido pelo servidor, então a lista inteira já está aqui: filtrar em memória é instantâneo
 *  e dispensa debounce, estado de carregando e o tratamento de respostas que voltam fora de ordem.
 *
 *  E há um detalhe que só a versão em memória acerta de graça: a busca aparece a partir de 10
 *  etiquetas, contando a lista INTEIRA. Se o filtro fosse do servidor, procurar algo que devolve
 *  dois resultados faria o próprio campo de busca desaparecer no meio da digitação.
 *  =======================================================================
 *
 *  ===================== ESTA TELA AINDA NÃO APLICA NADA =====================
 *  Ela cria o vocabulário. O seletor que cola a etiqueta no card vem depois, e será um componente
 *  compartilhado pelas três telas onde isso acontece — caixa de entrada, contato e funil —,
 *  seguindo o `nucleo/fechamento/modal-fechamento`, que já é usado exatamente por essas três.
 *
 *  ⚠️ É por isso que não há contagem de uso aqui. `EtapaDto` carrega `Contatos` porque o número
 *  decide se dá para apagar a etapa; aqui não existe tabela de ligação, e um número que é sempre
 *  zero só ensina a ignorá-lo.
 *  ========================================================================== */
@Component({
  selector: 'app-etiquetas',
  imports: [FormsModule, EtiquetaForm],
  templateUrl: './etiquetas.html',
  styleUrl: './etiquetas.css'
})
export class Etiquetas implements OnInit {
  private servico = inject(EtiquetasServico);
  private toast = inject(ToastServico);
  private injetor = inject(Injector);
  private host = inject(ElementRef<HTMLElement>);

  /** Espelha `ServicoEtiquetas.MaximoEtiquetas`. Duplicado de propósito: a tela esconde o
   *  formulário ANTES de o dono digitar um nome e levar 422. O servidor continua decidindo. */
  readonly maximo = 60;
  readonly buscaAPartirDe = BUSCA_A_PARTIR_DE;
  readonly sugestoes = SUGESTOES;

  lista = signal<EtiquetaNaLista[]>([]);
  carregando = signal(true);
  erro = signal('');
  salvando = signal(false);

  busca = signal('');
  ordem = signal<OrdemEtiquetas>('nome');

  /** Qual formulário está aberto: `'novo'`, o id de uma etiqueta em edição, ou nada.
   *
   *  Um só de cada vez, e é deliberado: dois formulários abertos disputariam o foco e o `Esc`, e
   *  ninguém edita duas etiquetas ao mesmo tempo. */
  editando = signal<number | 'novo' | null>(null);
  erroForm = signal('');

  /** A etiqueta cuja remoção está sendo confirmada. Sem destino, diferente de etapa: apagar não
   *  deixa contato órfão — ele só perde um rótulo. */
  removendo = signal<EtiquetaNaLista | null>(null);

  /** O impacto RELIDO no momento da confirmação. `null` = ainda buscando.
   *
   *  ⚠️ A lista já traz a contagem, e mesmo assim isto existe: a lista foi carregada quando a
   *  tela abriu, e entre aquele instante e o clique em "Apagar" outra pessoa pode ter marcado
   *  mais vinte contatos. A confirmação é o último lugar onde o dono ainda pode desistir, e o
   *  número ali tem de ser o de agora. */
  impacto = signal<number | null>(null);

  /** O nome digitado na confirmação, quando o impacto exige. */
  nomeConfirmacao = signal('');

  readonly digitarNomeAcimaDe = DIGITAR_NOME_ACIMA_DE;

  exigeDigitarNome = computed(() => (this.impacto() ?? 0) > DIGITAR_NOME_ACIMA_DE);

  podeApagar = computed(() => {
    const alvo = this.removendo();
    if (!alvo || this.impacto() === null) return false;
    if (!this.exigeDigitarNome()) return true;
    // Comparação sem diferenciar maiúscula, como o nome único: exigir a caixa exata seria
    // pedantismo numa confirmação que já é deliberadamente trabalhosa.
    return this.nomeConfirmacao().trim().toLowerCase() === alvo.nome.toLowerCase();
  });

  textoSobre = textoSobre;

  cheio = computed(() => this.lista().length >= this.maximo);
  mostrarBusca = computed(() => this.lista().length > BUSCA_A_PARTIR_DE);

  /** ⚠️ `localeCompare` com `pt-BR`, e não `<`. Comparação de string bruta ordena por ponto de
   *  código: "Ótimo" cairia depois de "Zebra", porque Ó é U+00D3. Numa lista que o dono escreve
   *  em português isso aparece na primeira palavra acentuada.
   *
   *  A ordem `recentes` inverte a lista do servidor, que vem por nome — não é o mesmo que ordenar
   *  por data. Funciona porque o `id` é sequencial e a lista está completa; se um dia a listagem
   *  ganhar paginação, isto tem de virar `ordem=recentes` na API, que já existe. */
  visiveis = computed(() => {
    const alvo = this.busca().trim().toLowerCase();
    const filtradas = alvo
      ? this.lista().filter(e => e.nome.toLowerCase().includes(alvo))
      : [...this.lista()];

    if (this.ordem() === 'recentes') return filtradas.sort((a, b) => b.id - a.id);

    // ⚠️ Desempata por NOME, como o servidor faz: numa lista de sessenta a maioria empata em zero
    // uso, e sem o desempate elas sairiam na ordem em que a resposta chegou.
    if (this.ordem() === 'uso') {
      return filtradas.sort(
        (a, b) => b.contatos - a.contatos || a.nome.localeCompare(b.nome, 'pt-BR'));
    }

    return filtradas.sort((a, b) => a.nome.localeCompare(b.nome, 'pt-BR'));
  });

  ngOnInit() { this.carregar(); }

  carregar() {
    this.carregando.set(true);
    // ⚠️ Sem `busca` nem `ordem`: a tela carrega SEMPRE a lista completa por nome, e o resto é em
    // memória. Ver o bloco no cabeçalho da classe.
    this.servico.listar().subscribe({
      next: l => { this.lista.set(l); this.carregando.set(false); this.erro.set(''); },
      error: () => {
        this.erro.set('Não foi possível carregar as etiquetas.');
        this.carregando.set(false);
      }
    });
  }

  // ---------------------------------------------------------------- abrir e fechar o formulário
  /** ===================== A CHAVE DO BOTÃO, E NÃO O BOTÃO =====================
   *  Guardar o `HTMLElement` que foi clicado não funciona aqui, e a razão é sutil: abrir o
   *  formulário troca o ramo de um `@if`, e o Angular DESTRÓI o bloco inteiro. Quando o
   *  formulário fecha, o botão que volta é um elemento NOVO — o antigo está desconectado, e
   *  chamar `.focus()` nele não faz nada. Silenciosamente.
   *
   *  Por isso o que fica guardado é uma chave (`editar-3`, `apagar-3`, `novo`), procurada no DOM
   *  depois que ele foi reconstruído.
   *  =========================================================================== */
  private origemDoFoco: string | null = null;

  abrirNovo() {
    this.origemDoFoco = 'novo';
    this.erroForm.set('');
    this.editando.set('novo');
  }

  editar(e: EtiquetaDto) {
    this.origemDoFoco = `editar-${e.id}`;
    this.erroForm.set('');
    this.editando.set(e.id);
  }

  fechar() {
    this.editando.set(null);
    this.erroForm.set('');
    this.devolverFoco();
  }

  private devolverFoco() {
    const chave = this.origemDoFoco;
    this.origemDoFoco = null;
    if (!chave) return;

    // ⚠️ `afterNextRender`, e não `setTimeout(0)`: o app é ZONELESS e o render é agendado por
    // `requestAnimationFrame`; um timer de 0 corre com ele e às vezes procura o botão antes de
    // ele voltar ao DOM. Mesmo raciocínio documentado em `nucleo/thread/thread.ts`.
    //
    // O `?.` cobre o caso legítimo de o botão não voltar — apagar a etiqueta leva a linha junto.
    // Aí o foco fica onde estava, que é melhor que pular para um lugar arbitrário.
    afterNextRender(() => {
      const alvo = (this.host.nativeElement as HTMLElement)
        .querySelector<HTMLElement>(`[data-foco="${chave}"]`);
      alvo?.focus();
    }, { injector: this.injetor });
  }

  valorInicial = computed<ValorEtiqueta>(() => {
    const alvo = this.editando();
    if (alvo === null || alvo === 'novo') return { nome: '', cor: '#5C8F6E' };

    const e = this.lista().find(x => x.id === alvo);
    return { nome: e?.nome ?? '', cor: e?.cor ?? '#5C8F6E' };
  });

  // ---------------------------------------------------------------- salvar
  salvar(valor: ValorEtiqueta) {
    const alvo = this.editando();
    if (alvo === null || this.salvando()) return;

    this.salvando.set(true);
    this.erroForm.set('');

    // `Observable<unknown>` porque criar devolve `{ id }` e atualizar devolve `void`: sem o tipo
    // comum, a uniao das duas assinaturas nao e chamavel.
    const requisicao: Observable<unknown> = alvo === 'novo'
      ? this.servico.criar(valor.nome, valor.cor)
      : this.servico.atualizar(alvo, valor.nome, valor.cor);

    requisicao.subscribe({
      next: () => {
        this.salvando.set(false);
        this.toast.sucesso(alvo === 'novo'
          ? `Etiqueta "${valor.nome}" criada.`
          : `Etiqueta "${valor.nome}" salva.`);
        this.editando.set(null);
        this.devolverFoco();
        this.carregar();
      },
      // A mensagem do servidor é a que importa — ela distingue "já existe" (409) de "cor inválida"
      // (400) de "chegou no limite" (422), e o dono precisa saber qual dos três foi. O formulário
      // fica ABERTO com o que foi digitado: fechar obrigaria a redigitar tudo para corrigir uma
      // letra.
      error: e => {
        this.salvando.set(false);
        this.erroForm.set(e.error?.erro ?? 'Não foi possível salvar.');
      }
    });
  }

  /** Cria direto a partir de uma sugestão do estado vazio, sem abrir formulário. O nome e a cor
   *  já são válidos — pedir confirmação de um clique que o usuário acabou de dar seria cerimônia. */
  criarSugestao(s: ValorEtiqueta) {
    if (this.salvando()) return;
    this.salvando.set(true);

    this.servico.criar(s.nome, s.cor).subscribe({
      next: () => {
        this.salvando.set(false);
        this.toast.sucesso(`Etiqueta "${s.nome}" criada.`);
        this.carregar();
      },
      error: e => {
        this.salvando.set(false);
        this.toast.erro(e.error?.erro ?? 'Não foi possível criar.');
      }
    });
  }

  // ---------------------------------------------------------------- remover
  /** ⚠️ PERGUNTA ANTES, mesmo sendo reversível em dois cliques hoje. Quando a etiqueta passar a
   *  colar em cards, apagar vai soltar todas as marcações de uma vez — e aí não é mais reversível.
   *  A confirmação nasce junto para o gesto não mudar de significado depois: confirmação que
   *  aparece só no dia em que a ação ficou perigosa é lida como estorvo novo. */
  confirmarRemocao(e: EtiquetaNaLista) {
    this.origemDoFoco = `apagar-${e.id}`;
    this.removendo.set(e);
    this.nomeConfirmacao.set('');
    this.impacto.set(null);

    // O botão de apagar só habilita quando o número chega — e se a chamada falhar ele fica
    // desabilitado, que é o lado certo do erro: melhor não apagar do que apagar às cegas.
    this.servico.impacto(e.id).subscribe({
      next: r => this.impacto.set(r.contatos),
      error: () => this.toast.erro('Não foi possível verificar o impacto. Tente de novo.')
    });
  }

  cancelarRemocao() {
    this.removendo.set(null);
    this.impacto.set(null);
    this.nomeConfirmacao.set('');
    this.devolverFoco();
  }

  remover() {
    const alvo = this.removendo();
    if (!alvo || this.salvando() || !this.podeApagar()) return;

    this.salvando.set(true);
    this.servico.remover(alvo.id).subscribe({
      next: () => {
        this.salvando.set(false);
        this.removendo.set(null);
        this.toast.info(`Etiqueta "${alvo.nome}" apagada.`);
        this.devolverFoco();
        this.carregar();
      },
      error: err => {
        this.salvando.set(false);
        this.removendo.set(null);
        this.toast.erro(err.error?.erro ?? 'Não foi possível apagar.');
      }
    });
  }

  /** `Esc` fecha o modal de exclusão. Mesmo gesto do formulário, pela mesma razão: é o que todo
   *  mundo tenta antes de procurar o botão. */
  aoTeclarNoModal(evento: KeyboardEvent) {
    if (evento.key === 'Escape') {
      evento.preventDefault();
      this.cancelarRemocao();
    }
  }
}
