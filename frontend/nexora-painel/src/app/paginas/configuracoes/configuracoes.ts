import { Component, ElementRef, OnInit, ViewChild, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { DatePipe } from '@angular/common';
import {
  Paginacao, fatiar, linhasFantasma, rolarParaTopoDaTabela, totalDePaginas
} from '../../nucleo/paginacao/paginacao';
import { ConfiguracaoServico } from '../../nucleo/servicos/configuracao.servico';
import { AuthServico } from '../../nucleo/servicos/auth.servico';
import { ToastServico } from '../../nucleo/toast/toast.servico';
import { ConfiguracaoEmpresa, FeriadoDto, FusoDisponivel } from '../../nucleo/modelos';

interface DiaSemana { bit: number; curto: string; nome: string; }

/** CONFIGURAÇÕES DA EMPRESA.
 *
 *  Cada campo daqui MUDA COMPORTAMENTO em algum lugar do sistema — se não muda, não é
 *  configuração e não entra nesta tela.
 *
 *  Só o DONO altera; gestor e vendedor leem. O enforcement é da API (403), e a tela esconde os
 *  controles: oferecer botão que sempre dá erro é pior que não oferecer. */
@Component({
  selector: 'app-configuracoes',
  imports: [FormsModule, DatePipe, Paginacao],
  templateUrl: './configuracoes.html',
  styleUrl: './configuracoes.css'
})
export class Configuracoes implements OnInit {
  private servico = inject(ConfiguracaoServico);
  private toast = inject(ToastServico);
  auth = inject(AuthServico);

  /** Bit 0 = domingo, seguindo o `DayOfWeek` do .NET — a mesma convenção do bitmask no banco. */
  readonly dias: DiaSemana[] = [
    { bit: 0, curto: 'D', nome: 'domingo' },
    { bit: 1, curto: 'S', nome: 'segunda' },
    { bit: 2, curto: 'T', nome: 'terça' },
    { bit: 3, curto: 'Q', nome: 'quarta' },
    { bit: 4, curto: 'Q', nome: 'quinta' },
    { bit: 5, curto: 'S', nome: 'sexta' },
    { bit: 6, curto: 'S', nome: 'sábado' }
  ];

  readonly horas = Array.from({ length: 25 }, (_, i) => i);

  config = signal<ConfiguracaoEmpresa | null>(null);
  carregando = signal(true);
  erro = signal('');

  // dados da empresa
  fNome = signal('');
  fDocumento = signal('');
  fFuso = signal('America/Sao_Paulo');
  fUf = signal('');
  salvandoDados = signal(false);
  erroDados = signal('');

  /** Vêm do servidor: a lista de fusos depende do tzdata do HOST, e oferecer um id que ele não
   *  conhece levaria o dono direto ao erro de validação. */
  fusos = signal<FusoDisponivel[]>([]);
  ufs = signal<string[]>([]);

  // atendimento
  fInicio = signal(8);
  fFim = signal(20);
  fDias = signal(126);
  fAmarelo = signal(60);
  fVermelho = signal(240);
  fDiasFollowUp = signal(2);
  salvandoAtendimento = signal(false);
  erroAtendimento = signal('');

  // feriados
  feriados = signal<FeriadoDto[]>([]);
  /** A API de feriados devolve o array inteiro (26 nacionais + estaduais + os da empresa). O
   *  recorte é de cliente pelo mesmo motivo da equipe: este bloco não muda API. */
  paginaFeriado = signal(1);
  @ViewChild('listaFeriados') private listaFeriados?: ElementRef<HTMLElement>;

  totalPaginasFeriado = computed(() => totalDePaginas(this.feriados().length));
  feriadosVisiveis = computed(() => fatiar(this.feriados(), this.paginaFeriado()));
  fantasmasFeriado = computed(() =>
    this.totalPaginasFeriado() > 1 ? linhasFantasma(this.feriadosVisiveis().length) : []);

  carregandoFeriados = signal(true);
  fFeriadoData = signal('');
  fFeriadoNome = signal('');
  salvandoFeriado = signal(false);
  erroFeriado = signal('');

  podeEditar = computed(() => this.auth.ehDono());

  /** Rótulo legível do bitmask, para o dono conferir sem contar bit. */
  resumoDias = computed(() => {
    const m = this.fDias();
    const marcados = this.dias.filter(d => (m & (1 << d.bit)) !== 0);
    if (marcados.length === 0) return 'nenhum dia — a empresa nunca atende';
    if (marcados.length === 7) return 'todos os dias';
    return marcados.map(d => d.nome).join(', ');
  });

  nenhumDia = computed(() => this.fDias() === 0);

  /** O fuso salvo está entre os que o servidor oferece? Se não, o select mostra o valor atual
   *  como opção extra — senão ele "pularia" para outro valor só por abrir a tela, e o dono
   *  salvaria a troca sem perceber. */
  temFusoNaLista = computed(() => this.fusos().some(f => f.id === this.fFuso()));

  ngOnInit() {
    this.carregar();
    this.carregarFeriados();

    // Listas de referência. Falhar aqui NÃO derruba a tela: os selects ficam com a opção atual
    // e o resto da configuração continua editável.
    this.servico.fusos().subscribe({ next: f => this.fusos.set(f), error: () => { } });
    this.servico.ufs().subscribe({ next: u => this.ufs.set(u), error: () => { } });
  }

  carregar() {
    this.carregando.set(true);
    this.servico.obter().subscribe({
      next: c => {
        this.config.set(c);
        this.fNome.set(c.nome);
        this.fDocumento.set(c.documento ?? '');
        this.fFuso.set(c.fusoHorario);
        this.fUf.set(c.uf ?? '');
        this.fInicio.set(c.janelaHoraInicio);
        this.fFim.set(c.janelaHoraFim);
        this.fDias.set(c.janelaDiasSemana);
        this.fAmarelo.set(c.semaforoAmareloMinutos);
        this.fVermelho.set(c.semaforoVermelhoMinutos);
        this.fDiasFollowUp.set(c.diasSemRespostaFollowUp);
        this.carregando.set(false);
        this.erro.set('');
      },
      error: () => {
        this.erro.set('Não foi possível carregar as configurações.');
        this.carregando.set(false);
      }
    });
  }

  // ---------------------------------------------------------------- dados
  salvarDados() {
    this.salvandoDados.set(true);
    this.erroDados.set('');
    this.servico.salvarDados(
      this.fNome().trim(), this.fDocumento().trim() || null,
      this.fFuso(), this.fUf() || null).subscribe({
      next: () => {
        this.salvandoDados.set(false);
        this.toast.sucesso('Dados da empresa salvos.');
        this.carregar();
      },
      error: e => {
        this.salvandoDados.set(false);
        this.erroDados.set(e.error?.erro ?? 'Não foi possível salvar.');
      }
    });
  }

  // ---------------------------------------------------------------- atendimento
  marcado(bit: number): boolean { return (this.fDias() & (1 << bit)) !== 0; }

  alternarDia(bit: number) {
    if (!this.podeEditar()) return;
    this.fDias.update(m => m ^ (1 << bit));
  }

  salvarAtendimento() {
    this.salvandoAtendimento.set(true);
    this.erroAtendimento.set('');
    this.servico.salvarAtendimento({
      janelaHoraInicio: this.fInicio(),
      janelaHoraFim: this.fFim(),
      janelaDiasSemana: this.fDias(),
      semaforoAmareloMinutos: this.fAmarelo(),
      semaforoVermelhoMinutos: this.fVermelho(),
      diasSemRespostaFollowUp: this.fDiasFollowUp()
    }).subscribe({
      next: () => {
        this.salvandoAtendimento.set(false);
        this.toast.sucesso('Atendimento salvo. Vale da próxima rodada de follow-up em diante.');
        this.carregar();
      },
      error: e => {
        this.salvandoAtendimento.set(false);
        this.erroAtendimento.set(e.error?.erro ?? 'Não foi possível salvar.');
      }
    });
  }

  /** Em horas, para o dono conferir o que digitou em minutos. */
  emHoras(minutos: number): string {
    if (minutos === 0) return 'desligado';
    if (minutos < 60) return `${minutos} min`;
    const h = minutos / 60;
    return Number.isInteger(h) ? `${h}h` : `${h.toFixed(1).replace('.', ',')}h`;
  }

  // ---------------------------------------------------------------- feriados
  carregarFeriados() {
    this.carregandoFeriados.set(true);
    this.servico.feriados().subscribe({
      next: f => {
        this.feriados.set(f);
        // Apagar o último feriado da página 3 não pode deixar a tela em "Página 3 de 2".
        if (this.paginaFeriado() > this.totalPaginasFeriado())
          this.paginaFeriado.set(this.totalPaginasFeriado());
        this.carregandoFeriados.set(false);
      },
      error: () => this.carregandoFeriados.set(false)
    });
  }

  irParaFeriado(p: number) {
    this.paginaFeriado.set(p);
    rolarParaTopoDaTabela(this.listaFeriados?.nativeElement);
  }

  adicionarFeriado() {
    const nome = this.fFeriadoNome().trim();
    if (!this.fFeriadoData()) { this.erroFeriado.set('Escolha a data.'); return; }
    if (!nome) { this.erroFeriado.set('Dê um nome ao feriado.'); return; }

    this.salvandoFeriado.set(true);
    this.erroFeriado.set('');
    this.servico.criarFeriado(this.fFeriadoData(), nome).subscribe({
      next: () => {
        this.salvandoFeriado.set(false);
        this.fFeriadoData.set('');
        this.fFeriadoNome.set('');
        this.toast.sucesso('Feriado adicionado.');
        this.carregarFeriados();
      },
      error: e => {
        this.salvandoFeriado.set(false);
        this.erroFeriado.set(e.error?.erro ?? 'Não foi possível adicionar.');
      }
    });
  }

  removerFeriado(f: FeriadoDto) {
    if (!confirm(`Apagar o feriado "${f.nome}"?`)) return;
    this.servico.removerFeriado(f.id).subscribe({
      next: () => { this.toast.info('Feriado apagado.'); this.carregarFeriados(); },
      error: e => this.toast.erro(e.error?.erro ?? 'Não foi possível apagar.')
    });
  }

  /** Nacional não se apaga — a linha é compartilhada por todos os tenants. O que a empresa faz
   *  é declarar que trabalha nesse dia, e isso vale só para ela. */
  alternarTrabalha(f: FeriadoDto) {
    const chamada = f.ignorado
      ? this.servico.observaFeriado(f.id)
      : this.servico.trabalhaNoFeriado(f.id);

    chamada.subscribe({
      next: () => {
        this.toast.sucesso(f.ignorado
          ? `A empresa volta a fechar no ${f.nome}.`
          : `A empresa passa a trabalhar no ${f.nome}.`);
        this.carregarFeriados();
      },
      error: e => this.toast.erro(e.error?.erro ?? 'Não foi possível alterar.')
    });
  }
}
